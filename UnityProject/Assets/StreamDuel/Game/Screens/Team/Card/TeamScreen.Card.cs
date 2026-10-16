using System.Linq;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class TeamScreen
    {
        TeamCardPanel cardPanel;
        Button openCardButton;

        void BuildCardEntry(VisualElement parent)
        {
            openCardButton = Button(parent, "team-open-card", "打开主播资料卡", OpenCardPanel);
            openCardButton.SetEnabled(false);
        }

        void RefreshCardEntry()
        {
            openCardButton?.SetEnabled(db != null && detailId != null
                && db.Streamers.Any(streamer => streamer.Id == detailId));
        }

        void OpenCardPanel()
        {
            RequireContent();
            if (detailId == null) return;
            if (cardPanel == null)
            {
                cardPanel = new TeamCardPanel(router.Resources.RegularFont, router.Resources.BoldFont, portraits,
                    id => { if (root != null) ShowDetail(id); },
                    () => { if (root != null) RefreshCardEntry(); });
            }
            cardPanel.Open(root, db, detailId, settings.ColorblindMode);
        }

        void CloseCardPanel() { cardPanel?.Close(); }
        void TickCardPanel(float dt) { cardPanel?.Tick(dt); }

        void DisposeCardPanel()
        {
            cardPanel?.Dispose();
            cardPanel = null;
            openCardButton = null;
        }
    }
}
