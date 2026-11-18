"""Independent next-format study; no CopperFS production imports or disk I/O.

Enumerate interrupted/reordered/torn sector outcomes around redo barriers.
Whole records stand for CRC-valid, identity-bound records; None stands for a
detected tear. The real two-sector descriptor is one abstract record here.
This is an abstract protocol model, not a filesystem emulator,
checksum implementation, benchmark, or proof against checksum collisions.
"""

import json
from itertools import product


CAPACITY = 48
SECTOR_BYTES = 512


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def boot_disk():
    descriptor = ("descriptor", 0, ())
    commit = ("commit", 0, 0, descriptor)
    return {
        "descriptor0": descriptor,
        "commit0": commit,
        "clean0": ("clean", commit),
        "home:a": ("page", 0, "a", "a0"),
        "home:b": ("page", 0, "b", "b0"),
    }


def selected_commit(disk):
    candidates = []
    for bank in (0, 1):
        record = disk.get(f"commit{bank}")
        if (isinstance(record, tuple) and len(record) == 4
                and record[0] == "commit" and record[2] == bank):
            candidates.append(record)
    require(bool(candidates), "No surviving commit")
    return max(candidates, key=lambda record: record[1])


def recover(disk):
    """Recover with successful barriers; return clean disk and selected state."""
    commit = selected_commit(disk)
    _, sequence, bank, expected_descriptor = commit
    clean = any(disk.get(f"clean{slot}") == ("clean", commit)
                for slot in (0, 1))
    result = dict(disk)
    if not clean:
        descriptor = disk.get(f"descriptor{bank}")
        require(descriptor == expected_descriptor, "Committed descriptor invalid")
        _, descriptor_sequence, entries = descriptor
        require(descriptor_sequence == sequence, "Descriptor generation mismatch")
        require(len(entries) <= CAPACITY, "Unbounded recovery")
        require(len({target for target, _ in entries}) == len(entries),
                "Duplicate home target")
        for index, (target, expected_page) in enumerate(entries):
            page = disk.get(f"image{bank}:{index}")
            require(page == expected_page, "Committed image invalid")
            require(page[:3] == ("page", sequence, target), "Image binding invalid")
            result[f"home:{target}"] = page
        # Home barrier, then clean marker write and clean-marker barrier.
        result[f"clean{bank}"] = ("clean", commit)
    values = {}
    for target in ("a", "b"):
        page = result.get(f"home:{target}")
        require(isinstance(page, tuple) and len(page) == 4
                and page[0] == "page" and page[2] == target,
                "Torn or misdirected home page exposed")
        values[target] = page[3]
    return result, sequence, values


def transaction(disk, changes):
    require(0 < len(changes) <= CAPACITY, "Invalid transaction size")
    current = selected_commit(disk)
    require(any(disk.get(f"clean{slot}") == ("clean", current)
                for slot in (0, 1)), "New mutation before prior cleanup")
    sequence = current[1] + 1
    bank = 1 - current[2]
    entries = tuple((target, ("page", sequence, target, value))
                    for target, value in sorted(changes.items()))
    descriptor = ("descriptor", sequence, entries)
    commit = ("commit", sequence, bank, descriptor)
    stages = [
        {**{f"image{bank}:{index}": page
            for index, (_, page) in enumerate(entries)},
         f"descriptor{bank}": descriptor},
        {f"commit{bank}": commit},
        {f"home:{target}": page for target, page in entries},
        {f"clean{bank}": ("clean", commit)},
    ]
    return sequence, stages


def interrupted_states(disk, stages):
    """Each sector can retain old bytes, persist new bytes, or detectably tear.

    Since no phase writes the same sector twice, this enumerates persisted
    subsets of every ordering. Each next phase starts only after the preceding
    successful barrier. Crash before/after barrier is included.
    """
    completed = dict(disk)
    for phase, writes in enumerate(stages, 1):
        keys = tuple(writes)
        for outcomes in product(("old", "new", "torn"), repeat=len(keys)):
            partial = dict(completed)
            for key, outcome in zip(keys, outcomes):
                if outcome == "new":
                    partial[key] = writes[key]
                elif outcome == "torn":
                    partial[key] = None
            yield phase, partial
        completed.update(writes)


def finish(disk, changes):
    _, stages = transaction(disk, changes)
    result = dict(disk)
    for writes in stages:
        result.update(writes)
    return result


def check_protocol():
    scenarios = 0
    required_replay = 0
    recovery_crashes = 0
    bank_reuses = 0
    # Multiple valid stale banks and untouched pages exist before each crash.
    baseline = boot_disk()
    for changes in ({"a": "a1", "b": "b1"}, {"a": "a2"}, {"b": "b3"}):
        baseline = finish(baseline, changes)
        bank_reuses += 1
    _, old_sequence, old_values = recover(baseline)
    changes = {"a": "a4", "b": "b4"}
    new_sequence, stages = transaction(baseline, changes)
    new_values = {**old_values, **changes}
    for phase, partial in interrupted_states(baseline, stages):
        selected = selected_commit(partial)
        if not any(partial.get(f"clean{slot}") == ("clean", selected)
                   for slot in (0, 1)):
            required_replay += 1
        repaired, recovered_sequence, recovered_values = recover(partial)
        expected = new_values if recovered_sequence == new_sequence else old_values
        require(recovered_sequence in (old_sequence, new_sequence), "Stale bank selected")
        require(recovered_values == expected, f"Mixed generation after phase {phase}")
        if phase >= 3:
            require(recovered_sequence == new_sequence, "Durable commit lost")
        require(recover(repaired)[1:] == (recovered_sequence, recovered_values),
                "Recovery is not idempotent")
        scenarios += 1
        if repaired != partial:
            _, _, bank, descriptor = selected
            repairs = [{f"home:{target}": page for target, page in descriptor[2]},
                       {f"clean{bank}": ("clean", selected)}]
            for _, interrupted_repair in interrupted_states(partial, repairs):
                _, repair_sequence, repair_values = recover(interrupted_repair)
                require((repair_sequence, repair_values)
                        == (recovered_sequence, recovered_values),
                        "Crash during recovery lost committed state")
                recovery_crashes += 1
        # Retry with a different target set, then crash again. Unpublished
        # images and an older commit must never contribute an untouched page.
        for target in ("a", "b"):
            retry_changes = {target: f"{target}-retry"}
            retry_sequence, retry_stages = transaction(repaired, retry_changes)
            retry_values = {**recovered_values, **retry_changes}
            for retry_phase, retry_partial in interrupted_states(repaired, retry_stages):
                _, actual_sequence, actual_values = recover(retry_partial)
                expected_retry = (retry_values if actual_sequence == retry_sequence
                                  else recovered_values)
                require(actual_sequence in (recovered_sequence, retry_sequence),
                        "Retry selected abandoned generation")
                require(actual_values == expected_retry, "Retry exposed abandoned page")
                if retry_phase >= 3:
                    require(actual_sequence == retry_sequence, "Retry durable commit lost")
                scenarios += 1

    # Removing the data/log barrier admits a committed torn image: reject it,
    # rather than accepting a model that would also pass a broken ordering.
    broken = dict(baseline)
    for writes in stages[:2]:
        broken.update(writes)
    broken["image0:0"] = None  # generation four uses bank zero
    rejected_broken_ordering = False
    try:
        recover(broken)
    except AssertionError:
        rejected_broken_ordering = True
    require(rejected_broken_ordering, "Missing first barrier went undetected")

    # A clean marker published ahead of the home barrier would expose mixed
    # pages. This ordering is deliberately absent from transaction().
    broken_clean = dict(baseline)
    for writes in stages[:2]:
        broken_clean.update(writes)
    broken_clean.update(stages[3])
    require(recover(broken_clean)[2] != new_values,
            "Negative clean-marker ordering control is ineffective")
    return {"crash_and_retry_outcomes": scenarios,
            "recovery_crash_outcomes": recovery_crashes,
            "first_crashes_requiring_replay": required_replay,
            "completed_warmup_transactions": bank_reuses,
            "negative_ordering_controls": 2}


def layout_budget():
    header_bytes = 64
    scalar_bytes = 256
    inline_bytes = SECTOR_BYTES - header_bytes - scalar_bytes
    extent_bytes = 24
    names = ("Renamed.bin", "Temp.bin")
    dirent_bytes = lambda name: (24 + len(name.encode("utf-8")) + 7) & ~7
    require(sum(map(dirent_bytes, names)) <= inline_bytes, "Mixed directory spills")
    require(24 <= inline_bytes, "One extent does not fit")
    descriptor_bytes = 2 * SECTOR_BYTES
    require(header_bytes + CAPACITY * 16 <= descriptor_bytes, "Descriptor overflow")
    memory = {"dirty_images": CAPACITY * SECTOR_BYTES,
              "operation_undo": CAPACITY * SECTOR_BYTES,
              "clean_cache": 16 * SECTOR_BYTES,
              "descriptors": descriptor_bytes,
              "bounded_index": 128 * 16,
              "control": 4096,
              "tree_path_scratch": 8 * SECTOR_BYTES,
              "name_scratch": 2 * 1024,
              "crc_tables": 4096}
    return {"sector_bytes": SECTOR_BYTES, "inline_bytes": inline_bytes,
            "inline_extent_capacity": inline_bytes // extent_bytes,
            "mixed_inline_directory_bytes": sum(map(dirent_bytes, names)),
            "mixed_distinct_dirty_pages_assuming_one_group_and_bitmap_window": 7,
            "journal_bank_bytes": (CAPACITY + 3) * SECTOR_BYTES,
            "core_scratch_components": memory,
            "core_scratch_subtotal_bytes": sum(memory.values()),
            "provisional_core_arena_ceiling_bytes": 96 * 1024}


if __name__ == "__main__":
    print(json.dumps({"status": "abstract-study-only",
                      "protocol": check_protocol(), "layout": layout_budget()}, indent=2))
