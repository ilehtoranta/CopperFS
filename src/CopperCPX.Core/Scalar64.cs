/*
- Copyright (C) 2026 Ilkka Lehtoranta
- SPDX-License-Identifier: MIT
*/

using System.Runtime.CompilerServices;
namespace CopperCPX;
internal static class Scalar64 {
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong Pack(uint high,uint low)=>((ulong)high<<32)|low;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong From(uint low)=>low;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint High(ulong value)=>(uint)(value>>32);
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Low(ulong value)=>(uint)value;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint High(Wide value)=>value.High;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Low(Wide value)=>value.Low;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool IsZero(ulong value)=>value==0;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool IsZero(Wide value)=>value.IsZero;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool Equal(ulong a,ulong b)=>a==b;
 public static int Compare(ulong a,ulong b)=>a==b?0:a<b?-1:1;
 public static bool Add(ulong a,ulong b,out ulong value){value=unchecked(a+b);return value>=a;}
 public static bool Subtract(ulong a,ulong b,out ulong value){value=unchecked(a-b);return a>=b;}
 public static bool Left(ulong a,int shift,out ulong value){if(shift<0||shift>=64){value=0;return false;}value=a<<shift;return shift==0||(a>>(64-shift))==0;}
 public static ulong Right(ulong a,int shift)=>shift<=0?a:shift>=64?0:a>>shift;
 public static ulong Multiply(uint a,uint b)=>(ulong)a*b;
}
