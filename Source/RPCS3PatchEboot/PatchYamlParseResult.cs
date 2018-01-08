using System;
using System.Collections.Generic;

namespace RPCS3PatchEboot
{
    public class PatchYamlParseResult
    {
        public List<PatchUnit> Patches { get; }

        public Exception Exception { get; set; }

        public bool Success => Exception == null;

        public PatchYamlParseResult()
        {
            Patches = new List< PatchUnit >();
        }
    }
}