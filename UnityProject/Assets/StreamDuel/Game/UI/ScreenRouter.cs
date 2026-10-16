using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class ScreenRouter : MonoBehaviour
    {
        // 热重载不能恢复工厂和委托，相关状态必须一起重建，不能只留下 initialized。
        [NonSerialized] readonly Dictionary<string, Func<IScreen>> factories = new Dictionary<string, Func<IScreen>>(StringComparer.Ordinal);
        [NonSerialized] UIDocument document;
        [NonSerialized] VisualElement root;
        [NonSerialized] IScreen current;
        [NonSerialized] bool initialized;
        [NonSerialized] bool restorePending;

        [field: NonSerialized]
        public ScreenResources Resources { get; private set; }
        public UIDocument Document => document;
        [field: NonSerialized]
        public string CurrentScreenId { get; private set; }
        [field: NonSerialized]
        public object CurrentArgs { get; private set; }
        internal bool IsInitialized => initialized;

        public void Initialize(UIDocument uiDocument, ScreenResources resources, Action<bool> legacyVisibility = null)
        {
            if (initialized) throw new InvalidOperationException("界面路由已经初始化。");
            if (uiDocument == null) throw new ArgumentNullException(nameof(uiDocument));
            if (resources == null || resources.PanelSettings == null || resources.Theme == null
                || resources.RegularFont == null || resources.BoldFont == null)
                throw new InvalidOperationException("界面资源不完整，请检查 StreamDuelUI 的面板、主题和中文字体。");
            document = uiDocument;
            Resources = resources;
            // 保留旧调用方的可选参数，但只允许关闭旧入口。
            legacyVisibility?.Invoke(false);
            document.panelSettings = resources.PanelSettings;
            initialized = true;
            TryBindRoot();
        }

        public void Register(string screenId, Func<IScreen> factory)
        {
            if (string.IsNullOrWhiteSpace(screenId)) throw new ArgumentException("界面编号不能为空。", nameof(screenId));
            if (screenId == "legacy") throw new ArgumentException("旧版界面已移除。", nameof(screenId));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            factories.Add(screenId, factory);
        }

        public void Show(string screenId, object args = null)
        {
            if (!initialized) throw new InvalidOperationException("界面路由尚未初始化。");
            if (!isActiveAndEnabled) throw new InvalidOperationException("界面路由尚未启用。");
            Func<IScreen> factory = null;
            if (screenId == null || !factories.TryGetValue(screenId, out factory))
                throw new ArgumentException("未注册的界面：" + screenId, nameof(screenId));
            if (!TryBindRoot())
            {
                CurrentScreenId = screenId;
                CurrentArgs = args;
                restorePending = true;
                return;
            }
            IScreen next = factory();
            if (next == null) throw new InvalidOperationException("界面工厂没有创建界面：" + screenId);
            VisualElement page;
            try
            {
                if (next.Id != screenId) throw new InvalidOperationException("界面编号与注册编号不一致：" + screenId);
                page = next.Build(this, args);
                if (page == null) throw new InvalidOperationException("界面没有返回根元素：" + screenId);
            }
            catch
            {
                next.Dispose();
                throw;
            }
            // 新页构建成功后才替换旧页，构建失败时保留当前页面。
            ReleaseCurrent();
            current = next;
            CurrentScreenId = screenId;
            CurrentArgs = args;
            restorePending = false;
            root.Add(page);
            root.style.display = DisplayStyle.Flex;
        }

        void Update()
        {
            if (!initialized || !RestorePage()) return;
            current?.Tick(Time.unscaledDeltaTime);
        }

        void OnEnable()
        {
            if (!initialized) return;
            restorePending = CurrentScreenId != null;
            RestorePage();
        }

        void OnDisable()
        {
            if (!initialized) return;
            restorePending = CurrentScreenId != null;
            ReleaseCurrent();
            if (root != null) root.style.display = DisplayStyle.None;
        }

        void OnDestroy()
        {
            ReleaseCurrent();
            factories.Clear();
            CurrentArgs = null;
        }

        bool TryBindRoot()
        {
            var latest = document != null && document.isActiveAndEnabled ? document.rootVisualElement : null;
            if (ReferenceEquals(latest, root)) return root != null;
            ReleaseCurrent();
            root = latest;
            restorePending = CurrentScreenId != null;
            if (root == null) return false;
            root.name = "screen-router";
            root.style.flexGrow = 1;
            root.style.unityFont = Resources.RegularFont;
            if (!root.styleSheets.Contains(Resources.Theme)) root.styleSheets.Add(Resources.Theme);
            root.style.display = DisplayStyle.None;
            return true;
        }

        bool RestorePage()
        {
            // UIDocument 重启时会换根元素；若其 OnEnable 尚未完成，留到后续 Update 恢复。
            if (!TryBindRoot()) return false;
            if (restorePending && CurrentScreenId != null) Show(CurrentScreenId, CurrentArgs);
            return true;
        }

        void ReleaseCurrent()
        {
            var previous = current;
            current = null;
            try { previous?.Dispose(); }
            finally { root?.Clear(); }
        }
    }
}
