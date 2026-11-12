using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WinLaunch
{
    /// <summary>
    /// 中文拼音首字母：利用 zh-CN 的拼音排序规则，用每个声母的“首字”做边界二分，
    /// 不需要内置几 MB 的拼音字典。常见多音字额外给出两种首字母，按变体匹配。
    /// </summary>
    public static class Pinyin
    {
        private static readonly CompareInfo Ci = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;

        // 与 Letters 一一对应：每个声母在拼音排序里的第一个字
        private static readonly string[] Bounds = new string[]
        {
            "阿", "八", "擦", "搭", "蛾", "发", "噶", "哈", "击", "喀", "垃",
            "妈", "拿", "哦", "啪", "期", "然", "撒", "塌", "挖", "昔", "压", "匝"
        };
        private const string Letters = "ABCDEFGHJKLMNOPQRSTWXYZ";

        // 常见多音字：给出两种首字母（声母不同时才需要列）
        private static readonly Dictionary<char, string> Polyphones = new Dictionary<char, string>
        {
            { '乐', "ly" }, { '行', "hx" }, { '重', "cz" }, { '长', "cz" }, { '传', "cz" },
            { '会', "hk" }, { '朝', "cz" }, { '藏', "cz" }, { '降', "jx" }, { '恶', "ew" },
            { '便', "bp" }, { '曾', "cz" }, { '单', "ds" }, { '解', "jx" }, { '区', "oq" },
            { '查', "cz" }, { '车', "cj" }, { '校', "xj" }
        };

        private const int MaxVariants = 64;

        /// <summary>名称的拼音/英文首字母变体，例如：微信 → wx，网易云音乐 → wyyyy / wyyyl。</summary>
        public static List<string> Variants(string name)
        {
            List<string> acc = new List<string>();
            acc.Add("");
            if (string.IsNullOrEmpty(name)) return acc;

            int i = 0;
            while (i < name.Length)
            {
                char c = name[i];
                string options;
                if (c >= '\u3400' && c <= '\u9FFF')
                {
                    options = OptionsFor(c);
                    i++;
                }
                else if (IsAsciiWordChar(c))
                {
                    int j = i;
                    while (j < name.Length && IsAsciiWordChar(name[j])) j++;
                    string word = name.Substring(i, j - i).ToLowerInvariant();
                    // 英文单词既支持首字母（vsc），也支持整词（qqy / wpso）
                    options = word.Length > 1 ? word + "|" + word[0] : word;
                    i = j;
                }
                else
                {
                    i++;
                    continue;
                }

                if (string.IsNullOrEmpty(options)) continue;

                List<string> next = new List<string>();
                foreach (string prefix in acc)
                {
                    foreach (string opt in options.Split('|'))
                    {
                        if (opt.Length == 0) continue;
                        string v = prefix + opt;
                        if (next.IndexOf(v) < 0) next.Add(v);
                        if (next.Count >= MaxVariants) break;
                    }
                    if (next.Count >= MaxVariants) break;
                }
                acc = next;
            }
            return acc;
        }

        private static bool IsAsciiWordChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
        }

        /// <summary>拼音/英文首字母是否包含查询串（不区分大小写）。</summary>
        public static bool Matches(string name, string query)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(query)) return false;
            string q = query.Trim();
            if (q.Length == 0) return false;

            foreach (string v in Variants(name))
            {
                if (v.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string OptionsFor(char c)
        {
            char primary = InitialOf(c);
            string poly;
            List<string> opts = new List<string>();
            if (Polyphones.TryGetValue(c, out poly))
            {
                foreach (char p in poly)
                {
                    string s = char.ToLowerInvariant(p).ToString();
                    if (!opts.Contains(s)) opts.Add(s);
                }
            }
            if (primary != '?')
            {
                string s = char.ToLowerInvariant(primary).ToString();
                if (!opts.Contains(s)) opts.Add(s);
            }
            return string.Join("|", opts.ToArray());
        }

        private static char InitialOf(char c)
        {
            string s = c.ToString();
            int lo = 0, hi = Bounds.Length - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Ci.Compare(s, Bounds[mid]) >= 0)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            if (best < 0) return '?';
            return Letters[best];
        }
    }
}
