using System;
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
        }

        public static Settings Load()
        {
            foreach (var path in new[] { PathFor(), PathFor() + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using (var fs = File.OpenRead(path))
                    {
                        var ser = new DataContractJsonSerializer(typeof(Settings));
                        var settings = (Settings)ser.ReadObject(fs);
                        if (settings == null) continue;
                        if (double.IsNaN(settings.Left) || double.IsInfinity(settings.Left)) settings.Left = 60;
                        if (double.IsNaN(settings.Top) || double.IsInfinity(settings.Top)) settings.Top = 900;
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
}
