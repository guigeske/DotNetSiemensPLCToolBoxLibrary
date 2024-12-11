using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DataCollectorConnect.Models.Enums;

namespace Siemens.PLC
{
    public class SiemensPlcEvent
    {
        public SiemensPlcEvent() { }

        public string Ip { get; set; }
        public SeverityCodes Type { get; set; }
        public string ProductionEntity { get; set; }
        public string TriggerStart { get; set; }
        public string TriggerEnd { get; set; }
        public Metadata Metadata { get; set; }
        public List<DataCollectorConnect.Models.Base.EventTranslation> Translations { get; set; }
    }

    public class Metadata
    {        
        public uint Id { get; set; }
        public bool IsDynamic { get; set; }
        public bool IsSymbolic { get; set; }
        public uint AlarmClass { get; set; }
        public string AlarmClassName { get; set; }
        public string SymbolicTag { get; set; }
        public string BlockName { get; set; }
        public string HmiName { get; set; }
        public string ImportFileName { get; set; }
    }

    public class SiemensEventTranslation
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Language { get; set; }
    }
}
