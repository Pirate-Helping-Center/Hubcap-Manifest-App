#nullable disable
using System;
using System.Collections.Generic;

namespace HubcapManifestApp.Tools.DepotDumper
{
    public class DumperConfig
    {
        public bool RememberPassword { get; set; }
        public bool DumpUnreleased { get; set; }
        public List<uint> TargetAppIds { get; set; } = new List<uint>();
        public bool UseQrCode { get; set; }
    }
}
