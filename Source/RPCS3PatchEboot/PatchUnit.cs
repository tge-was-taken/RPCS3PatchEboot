using System.Collections.Generic;

namespace RPCS3PatchEboot
{
    public class PatchUnit
    {
        public string Name { get; set; }

        public List<Patch> Patches { get; set; }

        public PatchUnit()
        {
            Patches = new List<Patch>();
        }

        public PatchUnit( string name )
        {
            Name = name;
            Patches = new List<Patch>();
        }

        public override string ToString()
        {
            return Name;
        }
    }
}
