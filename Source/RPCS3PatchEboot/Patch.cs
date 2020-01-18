namespace RPCS3PatchEboot
{
    public struct Patch
    {
        public PatchType Type { get; set; }

        public uint Offset { get; set; }

        public dynamic Value { get; set; }

        public Patch( PatchType type, uint offset, dynamic value )
        {
            Type = type;
            Offset = offset;
            Value = value;
        }

        public override string ToString()
        {
            return $"{Type} 0x{Offset:X8} {Value}";
        }
    }
}
