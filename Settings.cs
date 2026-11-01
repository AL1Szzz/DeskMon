using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DeskMon
{
    [DataContract]
    public sealed class Settings
    {
        [DataMember] public double Left = 60;
        [DataMember] public double Top = 900;
        [DataMember] public bool ClickThrough = false;
        [DataMember] public bool Pinned = true;
        [DataMember] public bool ShowCpuLoad = true;
        [DataMember] public bool ShowCpuTemp = true;
        [DataMember] public bool ShowGpuLoad = true;
        [DataMember] public bool ShowGpuTemp = true;
        [DataMember] public bool ShowFan = true;
        [DataMember] public bool ShowRam = true;
        [DataMember] public bool ShowVram = true;
        [DataMember] public bool ShowNet = true;
        [DataMember] public bool ShowPill = true;   // frosted capsule behind the readouts

        // Game-driven NVIDIA Digital Vibrance. Levels are 0-100 percent, matching
        // the NVIDIA Control Panel; 50 is the neutral desktop position.
        [DataMember] public bool VibranceEnabled = true;
        [DataMember] public int VibranceLevel = 50;
        [DataMember] public List<VibranceApp> VibranceApps = new List<VibranceApp>();

        private static readonly object SaveGate = new object();

        private static string PathFor() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DeskMon.settings.json");

        [OnDeserializing]
        private void ApplyDefaults(StreamingContext context)
        {
            // DataContractJsonSerializer bypasses field initializers for missing fields.
            Left = 60;
            Top = 900;
            ClickThrough = false;
            Pinned = ShowCpuLoad = ShowCpuTemp = ShowGpuLoad = ShowGpuTemp = true;
            ShowFan = ShowRam = ShowVram = ShowNet = ShowPill = true;
            VibranceEnabled = true;
            VibranceLevel = 50;
            VibranceApps = new List<VibranceApp>();
        }

        public static Settings Load()
        {
            foreach (var path in new[] { PathFor(), PathFor() + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    // Hand-edited files often carry a UTF-8 byte order mark, which the JSON
                    // reader rejects; strip it so the whole file is not silently discarded.
                    var bytes = File.ReadAllBytes(path);
                    var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                    using (var fs = new MemoryStream(bytes, skip, bytes.Length - skip, false))
                    {
                        var ser = new DataContractJsonSerializer(typeof(Settings));
                        var settings = (Settings)ser.ReadObject(fs);
                        if (settings == null) continue;
                        if (double.IsNaN(settings.Left) || double.IsInfinity(settings.Left)) settings.Left = 60;
                        if (double.IsNaN(settings.Top) || double.IsInfinity(settings.Top)) settings.Top = 900;
                        // A JSON "VibranceApps":null must not leave a null collection behind.
                        if (settings.VibranceApps == null) settings.VibranceApps = new List<VibranceApp>();
                        return settings;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("DeskMon settings load: " + ex.Message); }
            }
            return new Settings();
        }

        public void Save()
        {
            lock (SaveGate)
            {
                var path = PathFor();
                var temp = path + ".tmp";
                try
                {
                    var ser = new DataContractJsonSerializer(typeof(Settings));
                    using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        ser.WriteObject(fs, this);
                        fs.Flush(true);
                    }
                    // Keep the last complete settings even if writing or replacement fails.
                    if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                    else File.Move(temp, path);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("DeskMon settings save: " + ex.Message); }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
        }
    }

    /// <summary>One process and the Digital Vibrance level it should get while in front.</summary>
    [DataContract]
    public sealed class VibranceApp
    {
        /// <summary>Process name without extension, for example "cs2".</summary>
        [DataMember] public string Process;

        /// <summary>Level in percent, 0-100 where 50 leaves colour untouched.</summary>
        [DataMember] public int Level = 50;
    }
}
