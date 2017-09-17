namespace RPCS3PatchEboot
{
    public struct Patch
    {
        public PatchType Type { get; set; }

        public uint Offset { get; set; }

        public ulong Value { get; set; }

        public Patch( PatchType type, uint offset, ulong value )
        {
            Type = type;
            Offset = offset;
            Value = value;
        }

        public override string ToString()
        {
            return $"{Type} 0x{Offset:X8} 0x{Value:X8}";
        }
    }
}
