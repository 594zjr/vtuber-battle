using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;

namespace StreamDuel.Game
{
    public sealed partial class TeamScreen
    {
        void RequireContent()
        {
            if (db == null) throw new InvalidOperationException("没有可用内容包。");
        }

        TeamPreset CurrentPreset()
        {
            return new TeamPreset { Name = presetName.value ?? "", PackId = db?.PackId ?? "", Ids = new List<string>(selected) };
        }

        void SetSelection(TeamPreset preset)
        {
            selected.Clear();
            if (preset.Ids != null) selected.AddRange(preset.Ids);
            presetName.SetValueWithoutNotify(preset.Name ?? "");
        }

        void SwitchPack(ContentDb next)
        {
            if (db == next) return;
            if (db != null)
            {
                selections[db.PackId] = CurrentPreset(); seasons[db.PackId] = seasonId;
            }
            TeamPreset selection;
            string nextSeason;
            try
            {
                selection = selections.TryGetValue(next.PackId, out var remembered) ? remembered : options.LoadSelection?.Invoke(next);
                nextSeason = seasons.TryGetValue(next.PackId, out string savedSeason) ? savedSeason : options.LoadSeason?.Invoke(next);
            }
            catch
            {
                packField.SetValueWithoutNotify(db == null ? "" : packField.choices[packs.IndexOf(db)]);
                throw;
            }
            db = next; seasonId = nextSeason; detailId = null; settings.DefaultPack = next.PackId;
            SetSelection(selection ?? new TeamPreset { Name = "我的队伍", PackId = next.PackId });
            PopulatePack(); RememberView(); Refresh();
            Message("已切换内容包。队伍按本包的赛季规则重新校验。");
        }

        void Toggle(string id)
        {
            RequireContent(); db.Streamer(id); ShowDetail(id);
            if (selected.Contains(id)) selected.RemoveAll(value => value == id);
            else
            {
                if (selected.Count >= 6) { Message("队伍已有六位，请先点击队伍位移出一位。"); return; }
                selected.Add(id);
            }
            Remember(); Refresh();
        }

        void Remember()
        {
            if (db == null) return;
            selections[db.PackId] = CurrentPreset();
            try { options.Remember?.Invoke(CurrentPreset(), settings); }
            catch (Exception ex) { storageStatus.text = "当前选择已生效，但尚未保存：" + ex.Message; }
        }

        void RememberView()
        {
            if (db == null) return;
            seasons[db.PackId] = seasonId;
            try { options.RememberView?.Invoke(CurrentPreset(), seasonId, settings); }
            catch (Exception ex) { storageStatus.text = "当前设置已生效，但尚未保存：" + ex.Message; }
        }

        void ValidateSelection()
        {
            RequireContent();
            var issues = CurrentPreset().Check(db, seasonId);
            if (issues.Count > 0) throw new ArgumentException(string.Join("；", issues));
        }

        void ReloadPresets()
        {
            try
            {
                presets = store.Load() ?? throw new InvalidOperationException("预设仓库返回了空值。");
                libraryReady = true; storageStatus.text = ""; PopulatePresets();
            }
            catch (Exception ex)
            {
                libraryReady = false; presets = new TeamPresetList();
                presetsField.choices = new List<string>(); presetsField.SetValueWithoutNotify("");
                storageStatus.text = "无法读取预设，已停止写入以保留原数据：" + ex.Message + "；可点击刷新重试。";
            }
            Refresh();
        }

        void PopulatePresets(int index = 0)
        {
            presetsField.choices = presets.Items.Select((preset, i) => (i + 1) + ". " + preset.DisplayName + "〔" + preset.PackId + "〕").ToList();
            presetsField.SetValueWithoutNotify(presets.Items.Count == 0 ? "" : presetsField.choices[Math.Min(index, presets.Items.Count - 1)]);
        }

        void SavePreset()
        {
            ValidateSelection();
            if (!libraryReady) throw new InvalidOperationException("请先成功读取或刷新预设仓库。");
            var candidate = TeamPresetList.FromJson(presets.ToJson());
            var preset = CurrentPreset();
            int index = candidate.Items.FindIndex(item => item.PackId == preset.PackId && item.Name == preset.Name);
            if (index >= 0) candidate.Items[index] = preset;
            else { index = candidate.Items.Count; candidate.Items.Add(preset); }
            try
            {
                store.Save(candidate); presets = candidate;
                PopulatePresets(index); Refresh(); storageStatus.text = "";
                Message("预设已保存：" + preset.DisplayName + "。");
            }
            catch (Exception ex) { storageStatus.text = "预设保存失败，当前队伍仍保留：" + ex.Message; }
        }

        void LoadPreset()
        {
            if (!libraryReady || presetsField.index < 0 || presetsField.index >= presets.Items.Count)
                throw new InvalidOperationException("请先选择已保存的预设。");
            ImportPreset(presets.Items[presetsField.index]);
        }

        void ImportPreset(TeamPreset preset)
        {
            if (preset == null) throw new FormatException("队伍预设不能为空。");
            // 在找包前检查原始名单，隐藏内容包不能把具体下架原因掩盖为缺包。
            string policyError = options.Policy?.CheckTeam(preset.PackId, preset.Ids);
            if (policyError != null) throw new ArgumentException(policyError);
            foreach (var pack in packs)
            {
                string disabled = pack.Policy.CheckTeam(preset.PackId, preset.Ids);
                if (disabled != null) throw new ArgumentException(disabled);
            }
            var next = packs.FirstOrDefault(pack => pack.PackId == preset.PackId)
                ?? throw new ArgumentException("尚未加载队伍所属的内容包：" + preset.PackId);
            string nextSeason = db == next ? seasonId : null;
            var issues = preset.Check(next, nextSeason);
            if (issues.Count > 0) throw new ArgumentException(string.Join("；", issues));
            if (db != null) selections[db.PackId] = CurrentPreset();
            db = next; seasonId = nextSeason; settings.DefaultPack = next.PackId;
            SetSelection(preset); PopulatePack(); RememberView(); Remember(); Refresh();
            Message("已读取队伍：" + preset.DisplayName + "。");
        }

        void CopyCode()
        {
            ValidateSelection();
            string code = TeamCode.Encode(CurrentPreset());
            codeField.SetValueWithoutNotify(code);
            if (options.WriteClipboard != null) options.WriteClipboard(code);
            else GUIUtility.systemCopyBuffer = code;
            Message("队伍码已复制，可发送给使用相同内容包的玩家。");
        }

        void PasteCode()
        {
            string code = options.ReadClipboard != null ? options.ReadClipboard() : GUIUtility.systemCopyBuffer;
            codeField.SetValueWithoutNotify(code ?? "");
            ImportPreset(TeamCode.Decode(code));
        }

        void StartBattle()
        {
            ValidateSelection();
            if (options.StartBattle == null) throw new InvalidOperationException("当前页面未连接对战入口。");
            var request = new TeamBattleRequest
            {
                Content = db, Team = CurrentPreset(), SeasonId = seasonId,
                Settings = GameSettings.FromJson(settings.ToJson()), Seed = options.Seed
            };
            var callback = options.StartBattle;
            bool navigate = options.NavigateToBattle;
            var activeRouter = router;
            Remember(); callback(request);
            if (navigate) activeRouter.Show(ScreenIds.Legacy, request);
            else Message("队伍和设置已交给对战界面。");
        }
    }
}
