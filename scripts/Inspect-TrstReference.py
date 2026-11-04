"""Independent native-reference manifest. Requires the pinned amidisk on PYTHONPATH."""
import hashlib
import json
import sys

from amidisk.image import open_image

with open_image(sys.argv[1]) as image:
    volume = image.get_volume("DH0").mount()
    report = volume.check(deep=True)
    if not report["ok"]:
        raise RuntimeError(report)
    entries = []

    def walk(path):
        for entry in volume.list_dir(path):
            name = f"{path}/{entry.name_str()}" if path else entry.name_str()
            digest = None
            if entry.is_file():
                checksum = hashlib.sha256()
                for chunk in volume.read_file(name):
                    checksum.update(chunk)
                digest = checksum.hexdigest()
            entries.append(dict(path=name, type=entry.type_str(), bits=entry.bits,
                                size=entry.size if entry.is_file() else 0,
                                sha256=digest, protection=entry.protect,
                                comment=entry.comment.decode("latin-1"), modified=entry.secs))
            if entry.is_dir():
                walk(name)

    walk("")
    print(json.dumps(dict(freeBlocks=volume.freeblocks,
                          entries=sorted(entries, key=lambda entry: entry["path"])), sort_keys=True))
