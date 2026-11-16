namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // The engine already admitted and sealed every descriptor in phase 100.
    // Publish that owned plan directly, with the engine's one I/O identity.
    // The standalone public publication kernel retains its own admission API.
    // Plan header occupies 64 bytes before the descriptors at undo + 1024.
    private StepResult BeginOwnedShadowPublication(ulong root)
    {
        uint storage=ShadowStorage,plan=BufferedUndo(0),count=memory.Read32(storage+60);
        uint survivor=memory.Read32(storage+32);
        if(count>42||survivor>2||!Scalar64.Add(RW(Generation),1,out var transaction))
            return Fail(CpxError.Corrupt);
        Clear(plan,64);
        memory.Write32(plan+12,uint.MaxValue); memory.Write32(plan+16,survivor);
        PutWide(plan+24,root); PutWide(plan+32,transaction);
        memory.Write32(plan+40,ShadowRootBytes); memory.Write32(plan+44,ShadowRootSpan);
        memory.Write32(plan+48,ShadowRootInput); memory.Write32(plan+52,count);
        memory.Write32(storage,3); memory.Write32(Arena+Phase,103);
        return StepResult.Pending;
    }

    private StepResult OwnedShadowPublicationStep()
    {
        uint plan=BufferedUndo(0),phase=memory.Read32(plan);
        if(phase==0)
        {
            uint cursor=memory.Read32(plan+4);
            if(cursor<memory.Read32(plan+52))
            {
                uint descriptor=plan+1024+cursor*32;
                ulong address=GetWide(descriptor)+(1-memory.Read32(descriptor+20))*Q;
                memory.Write32(plan+4,cursor+1);
                return Issue(IoKind.Write,address,memory.Read32(descriptor+16),P,103);
            }
            memory.Write32(plan,1);
            return Issue(IoKind.Flush,0,0,0,103);
        }
        if(phase==1)
        {
            uint survivor=memory.Read32(plan+16);
            if(BufferedWrites&&survivor!=1)
            {
                // Both non-surviving slots are adjacent when an outer slot
                // survives. Metadata images have quiesced and passed their
                // barrier, so this part of the undo bank no longer has users.
                uint first=survivor==0?1u:0u,pairBuffer=plan+4096,pairBytes=memory.Read32(plan+40),span=memory.Read32(plan+44);
                ulong pairAddress=Anchor+(first+1)*span;
                MakeOwnedShadowCheckpoint(pairBuffer,pairBytes,pairAddress,GetWide(plan+32),GetWide(plan+24));
                Copy(pairBuffer+pairBytes,pairBuffer,pairBytes);
                CpxShadowChecksums<TMemory>.RetargetOwned(ref memory,pairBuffer+pairBytes,pairBytes,pairAddress+span,CrcTable,BufferedStorageAddress,Arena+4080);
                memory.Write32(plan+12,survivor==0?2u:0u); memory.Write32(plan,3);
                return Issue(IoKind.Write,pairAddress,pairBuffer,2*pairBytes,103);
            }
            uint root=memory.Read32(plan+8);
            if(root==memory.Read32(plan+16)) root++;
            if(root==3)
            {
                memory.Write32(plan,2);
                return Issue(IoKind.Flush,0,0,0,103);
            }
            uint buffer=memory.Read32(plan+48),bytes=memory.Read32(plan+40);
            ulong address=Anchor+(root+1)*memory.Read32(plan+44);
            if(memory.Read32(plan+12)!=uint.MaxValue&&BufferedWrites)
                CpxShadowChecksums<TMemory>.RetargetOwned(ref memory,buffer,bytes,address,CrcTable,BufferedStorageAddress,Arena+4080);
            else MakeOwnedShadowCheckpoint(buffer,bytes,address,GetWide(plan+32),GetWide(plan+24));
            if(memory.Read32(plan+12)==uint.MaxValue) memory.Write32(plan+12,root);
            memory.Write32(plan+8,root+1);
            return Issue(IoKind.Write,address,buffer,bytes,103);
        }
        if(phase==3)
        {
            memory.Write32(plan,2);
            return Issue(IoKind.Flush,0,0,0,103);
        }
        if(phase==2)
        {
            uint storage=ShadowStorage;
            PutWide(storage+24,GetWide(plan+24)); memory.Write32(storage+32,memory.Read32(plan+12));
            memory.Write32(storage,0); ClearShadowCache();
            // CommitStep advances the mount generation and retires the group
            // only after the checkpoint persistence barrier above completes.
            memory.Write32(Arena+Phase,15); return StepResult.Pending;
        }
        return Fail(CpxError.Corrupt);
    }

    private void MakeOwnedShadowCheckpoint(uint buffer,uint bytes,ulong address,ulong transaction,ulong root)
    {
        uint control=ShadowRootControl?ShadowControl:0;
        bool catalogue=ShadowCatalogue;
        uint end=control==0?96u:catalogue?1024u:496u;
        Clear(buffer,96);
        if(control!=0) Copy(buffer+96,control+96,end-96);
        Clear(buffer+end,bytes-end);
        memory.Write32(buffer,0x43505835); memory.Write16(buffer+4,2); memory.Write16(buffer+6,5);
        PutWide(buffer+16,address); PutWide(buffer+32,transaction); memory.Write16(buffer+52,32);
        PutWide(buffer+72,RW(Identity)); PutWide(buffer+80,transaction); PutWide(buffer+88,root);
        if(control!=0)
        {
            memory.Write16(buffer+52,catalogue?960u:432u);
            memory.Write16(buffer+56,memory.Read16(control+56)); memory.Write16(buffer+58,32);
        }
        memory.Write32(buffer+8,Crc(buffer,bytes,true));
    }
}
