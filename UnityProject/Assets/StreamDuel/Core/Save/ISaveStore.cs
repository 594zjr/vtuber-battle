using System.Collections.Generic;

namespace StreamDuel.Core
{
    public interface ISaveStore
    {
        void SaveSettings(GameSettings settings);
        GameSettings LoadSettings();
        void SaveProfile(Profile profile);
        Profile LoadProfile();
        void SaveReplay(string name, string json);
        List<string> ListReplays();
        string LoadReplay(string name);
    }
}
