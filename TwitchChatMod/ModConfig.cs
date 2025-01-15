using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TwitchChatMod
{
    public class ModConfig
    {
        public string TwitchChannel { get; set; } = "";
        public List<string> IgnoredUsernames { get; set; } = new List<string>();
        public float ChatWidthScale { get; set; } = 0.6f;

        public int MaxMessages { get; set; } = 6;
        public bool ShowChatIngame { get; set; } = true;
    }
}
