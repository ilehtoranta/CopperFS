namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // The proof occupies eight bytes after the control backing and before the
    // independent allocation hint. It describes an authenticated owned image.
    // Media is still read on every mount; this proof only authenticates an
    // exact byte match under the same geometry, feature level and identity.
    private uint ControlProofAddress => ShadowStorage + ShadowStorageBytes(P) - 16;

    private void ForgetControlProof() => memory.Write32(ControlProofAddress, 0);

    private void RememberControlProof(uint bytes)
    {
        uint proof = ControlProofAddress;
        memory.Write32(proof + 4, memory.Read32(ShadowStorage + 56));
        memory.Write32(proof, bytes);
    }

    private bool CanReuseControlProof(uint bytes)
    {
        uint proof = ControlProofAddress, control = ShadowControl, context = Arena;
        return memory.Read32(proof) == bytes && memory.Read32(proof + 4) == memory.Read32(ShadowStorage + 56) &&
            memory.Read32(control + 72) == memory.Read32(context + Identity) &&
            memory.Read32(control + 76) == memory.Read32(context + Identity + 4);
    }

    // Called only after the replacement-root persistence barrier completed.
    // Publication copied the control body unchanged and zeroed its padding.
    // Copy its authenticated header and mirror that padding before recording
    // the proof; neither an earlier write reply nor a failed barrier gets here.
    private void RememberPublishedControlProof(uint source, uint bytes)
    {
        uint control = ShadowControl, end = ShadowCatalogue ? 1024u : 496u;
        Copy(control, source, 96);
        Clear(control + end, bytes - end);
        RememberControlProof(bytes);
    }

    private uint ShadowCheckpointChecksumWithControlProof(uint incoming, uint bytes, ulong address, uint validated, bool rootControl, out uint proof)
    {
        proof = 0;
        if (rootControl && CanCacheMetadata)
        {
            if (memory.Read32(Arena + ValidOne) != 0 && CheckpointBodyMatches(incoming, validated, bytes))
            {
                proof = 1;
                return CpxShadowChecksums<TMemory>.ExpectedRetarget(ref memory, validated, bytes, address, CrcTable, BufferedStorageAddress, Arena + 4080);
            }
            if (CanReuseControlProof(bytes) && CheckpointBodyMatches(incoming, ShadowControl, bytes))
            {
                proof = 2;
                return CpxShadowChecksums<TMemory>.ExpectedRetarget(ref memory, ShadowControl, bytes, address, CrcTable, BufferedStorageAddress, Arena + 4080);
            }
        }
        return Crc(incoming, bytes, true);
    }
}
