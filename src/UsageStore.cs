using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace WinLaunch
{
    [DataContract]
    public class UsageEntry
    {
        [DataMember(Name = "count")] public int Count;
        [DataMember(Name = "last")] public long LastTicks;
    }

    [DataContract]
    public class UsageData
    {
        [DataMember(Name = "items")] public Dictionary<string, UsageEntry> Items =
            new Dictionary<string, UsageEntry>();
    }

    /// <summary>
    /// 使用频次记录：每次成功启动累加一次，按"次数 × 时间衰减"排序，
    /// 供启动台顶部"常用"一行使用。数据单独存 usage.json，不污染 config。
    /// </summary>
    public static class UsageStore
    {
        private static readonly object Gate = new object();
        private static UsageData _data = new UsageData();
        private const int MaxRecords = 300;
        private const double HalfLifeDays = 21.0;

        public static string FilePath
        {
            get { return Path.Combine(ConfigStore.AppDir, "usage.json"); }
        }

        private static DataContractJsonSerializerSettings SimpleSettings()
        {
            DataContractJsonSerializerSettings s = new DataContractJsonSerializerSettings();
            s.UseSimpleDictionaryFormat = true;
            return s;
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                byte[] raw = File.ReadAllBytes(FilePath);
                int offset = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
                using (MemoryStream ms = new MemoryStream(raw, offset, raw.Length - offset))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(
                        typeof(UsageData), SimpleSettings());
                    UsageData d = ser.ReadObject(ms) as UsageData;
                    if (d != null && d.Items != null) _data = d;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UsageStore.Load", ex);
            }
        }

        /// <summary>记录一次启动。</summary>
        public static void Touch(AppEntry e)
        {
            if (e == null || string.IsNullOrEmpty(e.Id)) return;
            lock (Gate)
            {
                UsageEntry u;
                if (!_data.Items.TryGetValue(e.Id, out u))
                {
                    u = new UsageEntry();
                    _data.Items[e.Id] = u;
                }
                u.Count = u.Count + 1;
                u.LastTicks = DateTime.UtcNow.Ticks;

                if (_data.Items.Count > MaxRecords) PruneLocked();
                SaveLocked();
            }
        }

        /// <summary>按频次取前 n 个应用（映射回当前应用清单，找不到的自动跳过）。</summary>
        public static List<AppEntry> Top(int n)
        {
            List<AppEntry> result = new List<AppEntry>();
            if (n <= 0) return result;

            List<KeyValuePair<string, double>> scored = new List<KeyValuePair<string, double>>();
            lock (Gate)
            {
                DateTime now = DateTime.UtcNow;
                foreach (KeyValuePair<string, UsageEntry> kv in _data.Items)
                {
                    if (kv.Value == null || kv.Value.Count <= 0) continue;
                    double ageDays = 0;
                    try
                    {
                        ageDays = (now - new DateTime(kv.Value.LastTicks, DateTimeKind.Utc)).TotalDays;
                    }
                    catch { }
                    if (ageDays < 0) ageDays = 0;
                    double score = kv.Value.Count * Math.Exp(-ageDays / HalfLifeDays);
                    scored.Add(new KeyValuePair<string, double>(kv.Key, score));
                }
            }

            scored.Sort(delegate(KeyValuePair<string, double> a, KeyValuePair<string, double> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            foreach (KeyValuePair<string, double> kv in scored)
            {
                AppEntry found = null;
                foreach (AppEntry e in Core.Catalog)
                {
                    if (e.Id == kv.Key) { found = e; break; }
                }
                if (found != null && !result.Contains(found)) result.Add(found);
                if (result.Count >= n) break;
            }
            return result;
        }

        private static void PruneLocked()
        {
            List<KeyValuePair<string, UsageEntry>> all = new List<KeyValuePair<string, UsageEntry>>();
            foreach (KeyValuePair<string, UsageEntry> kv in _data.Items) all.Add(kv);
            all.Sort(delegate(KeyValuePair<string, UsageEntry> a, KeyValuePair<string, UsageEntry> b)
            {
                return b.Value.Count.CompareTo(a.Value.Count);
            });
            Dictionary<string, UsageEntry> kept = new Dictionary<string, UsageEntry>();
            for (int i = 0; i < all.Count && i < MaxRecords / 2; i++) kept[all[i].Key] = all[i].Value;
            _data.Items = kept;
        }

        private static void SaveLocked()
        {
            try
            {
                string tmp = FilePath + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(
                        typeof(UsageData), SimpleSettings());
                    ser.WriteObject(fs, _data);
                }
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, true);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Logger.Error("UsageStore.Save", ex);
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                _data = new UsageData();
                SaveLocked();
            }
        }
    }
}
