using System;
using System.Collections.ObjectModel;

namespace ViberManager.Models
{
    public class ViberManagerConfig
    {
        public string ViberPath { get; set; } = string.Empty;
        public ObservableCollection<ViberProfile> Profiles { get; set; } = new();
    }
}
