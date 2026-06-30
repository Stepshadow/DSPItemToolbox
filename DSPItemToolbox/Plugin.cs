using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace DSPItemToolbox
{
    [BepInPlugin("com.wyp.dspitemtoolbox", "DSP Item Toolbox", "0.7.0")]
    public class Plugin : BaseUnityPlugin
    {
        private const float WindowWidth = 820f;
        private const float WindowHeight = 590f;

        private static Plugin instance;

        private Harmony harmony;

        private bool toolboxOpen = false;
        private int closeGuardUntilFrame = -1;

        private Rect windowRect = new Rect(0, 0, WindowWidth, WindowHeight);
        private Vector2 scrollPosition = Vector2.zero;

        private string searchText = "";
        private string amountText = "100";

        private string message = "输入名称或 ID 搜索，点击物品即可刷取。";

        private readonly List<ItemProto> allItems = new List<ItemProto>();
        private readonly List<ItemProto> filteredItems = new List<ItemProto>();

        private readonly Dictionary<int, Sprite> itemIconCache =
            new Dictionary<int, Sprite>();

        private bool stylesReady = false;

        private Texture2D whiteTexture;
        private Texture2D panelTexture;
        private Texture2D headerTexture;
        private Texture2D inputTexture;
        private Texture2D buttonTexture;
        private Texture2D buttonHoverTexture;
        private Texture2D buttonActiveTexture;
        private Texture2D itemTexture;
        private Texture2D buildingItemTexture;

        private GUIStyle windowStyle;
        private GUIStyle titleStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle labelStyle;
        private GUIStyle smallLabelStyle;
        private GUIStyle inputStyle;
        private GUIStyle buttonStyle;
        private GUIStyle compactButtonStyle;
        private GUIStyle closeButtonStyle;
        private GUIStyle itemRowStyle;
        private GUIStyle buildingItemRowStyle;
        private GUIStyle itemNameStyle;
        private GUIStyle idStyle;
        private GUIStyle tagStyle;
        private GUIStyle footerStyle;
        private GUIStyle iconFallbackStyle;

        private bool inputLockSaved = false;
        private bool oldOnGUIOperate = false;

        private Type vfInputType;

        private readonly Dictionary<string, MemberInfo> vfInputMemberCache =
            new Dictionary<string, MemberInfo>();

        private readonly Dictionary<string, MethodInfo> vfInputMethodCache =
            new Dictionary<string, MethodInfo>();

        private void Awake()
        {
            instance = this;

            harmony = new Harmony("com.wyp.dspitemtoolbox");
            PatchGameCamera();

            Logger.LogInfo("DSP Item Toolbox 已加载");
        }

        private void Update()
        {
            bool togglePressed =
                Input.GetKeyDown(KeyCode.Equals) &&
                !Input.GetKey(KeyCode.LeftShift) &&
                !Input.GetKey(KeyCode.RightShift);

            if (togglePressed)
            {
                if (toolboxOpen)
                {
                    CloseToolbox();
                }
                else
                {
                    OpenToolbox();
                }
            }

            MaintainInputLock();
        }

        private void LateUpdate()
        {
            MaintainInputLock();
        }

        private void OnDisable()
        {
            RestoreGameInput();
        }

        private void OnDestroy()
        {
            RestoreGameInput();

            if (harmony != null)
            {
                harmony.UnpatchSelf();
                harmony = null;
            }

            instance = null;
        }

        private void PatchGameCamera()
        {
            try
            {
                Type gameCameraType = AccessTools.TypeByName("GameCamera");

                if (gameCameraType == null)
                {
                    Logger.LogWarning("未找到 GameCamera，无法启用相机输入锁。");
                    return;
                }

                MethodInfo frameLogic = AccessTools.Method(
                    gameCameraType,
                    "FrameLogic"
                );

                MethodInfo prefix = typeof(Plugin).GetMethod(
                    "GameCameraFrameLogicPrefix",
                    BindingFlags.NonPublic | BindingFlags.Static
                );

                if (frameLogic == null || prefix == null)
                {
                    Logger.LogWarning("未找到 GameCamera.FrameLogic，无法启用相机输入锁。");
                    return;
                }

                harmony.Patch(
                    frameLogic,
                    prefix: new HarmonyMethod(prefix)
                );

                Logger.LogInfo("相机输入锁补丁已加载。");
            }
            catch (Exception e)
            {
                Logger.LogWarning("相机输入锁补丁失败：" + e.Message);
            }
        }

        private static bool GameCameraFrameLogicPrefix()
        {
            if (instance == null)
            {
                return true;
            }

            if (!instance.ShouldBlockGameInput())
            {
                return true;
            }

            instance.ClearCameraInput();

            return false;
        }

        private bool ShouldBlockGameInput()
        {
            return toolboxOpen ||
                   Time.frameCount <= closeGuardUntilFrame;
        }

        private void MaintainInputLock()
        {
            if (ShouldBlockGameInput())
            {
                LockGameInput();
                ClearCameraInput();
            }
            else
            {
                RestoreGameInput();
            }
        }

        private void OpenToolbox()
        {
            toolboxOpen = true;
            closeGuardUntilFrame = -1;

            LockGameInput();

            windowRect.x = (Screen.width - WindowWidth) / 2f;
            windowRect.y = (Screen.height - WindowHeight) / 2f;

            LoadItems();
        }

        private void CloseToolbox()
        {
            toolboxOpen = false;

            // 防止关闭窗口那一下鼠标点到地图。
            closeGuardUntilFrame = Time.frameCount + 3;

            LockGameInput();
        }

        private void LoadItems()
        {
            allItems.Clear();
            itemIconCache.Clear();

            if (LDB.items == null || LDB.items.dataArray == null)
            {
                message = "物品数据库尚未加载，请进入存档后再打开。";
                return;
            }

            ItemProto[] items = LDB.items.dataArray;

            for (int i = 0; i < items.Length; i++)
            {
                ItemProto item = items[i];

                if (item == null || item.ID <= 0)
                {
                    continue;
                }

                allItems.Add(item);
            }

            RefreshItems();

            message = "数据库扫描完成：已载入 " +
                      allItems.Count +
                      " 个物品。";
        }

        private void RefreshItems()
        {
            filteredItems.Clear();

            string keyword = searchText.Trim().ToLower();

            for (int i = 0; i < allItems.Count; i++)
            {
                ItemProto item = allItems[i];

                string itemName = item.name == null ? "" : item.name;

                if (keyword != "")
                {
                    bool matchName = itemName.ToLower().Contains(keyword);
                    bool matchId = item.ID.ToString().Contains(keyword);

                    if (!matchName && !matchId)
                    {
                        continue;
                    }
                }

                filteredItems.Add(item);
            }

            scrollPosition = Vector2.zero;
        }

        private void GiveItem(ItemProto item)
        {
            int amount;

            if (!int.TryParse(amountText, out amount))
            {
                message = "数量必须是整数。";
                return;
            }

            if (amount <= 0 || amount > 1000000)
            {
                message = "数量范围应为 1 到 1000000。";
                return;
            }

            if (GameMain.mainPlayer == null)
            {
                message = "未检测到玩家，请进入存档后再刷取。";
                return;
            }

            int addedCount = GameMain.mainPlayer.TryAddItemToPackage(
                item.ID,
                amount,
                0,
                true,
                0
            );

            if (addedCount > 0)
            {
                UIItemup.Up(item.ID, addedCount);
            }

            if (addedCount == amount)
            {
                message = "物资已转入背包：" +
                          item.name +
                          " × " +
                          addedCount;
            }
            else if (addedCount > 0)
            {
                message = "背包容量不足，仅获得：" +
                          item.name +
                          " × " +
                          addedCount;
            }
            else
            {
                message = "背包没有可用空间。";
            }
        }

        private void OnGUI()
        {
            if (!toolboxOpen)
            {
                if (Time.frameCount <= closeGuardUntilFrame)
                {
                    ConsumeToolboxGUIEvent();
                }

                return;
            }

            EnsureStyles();

            DrawRect(
                new Rect(0, 0, Screen.width, Screen.height),
                new Color(0f, 0.02f, 0.04f, 0.55f)
            );

            DrawRect(
                new Rect(
                    windowRect.x + 7,
                    windowRect.y + 9,
                    windowRect.width,
                    windowRect.height
                ),
                new Color(0f, 0f, 0f, 0.45f)
            );

            windowRect = GUI.Window(
                100861,
                windowRect,
                DrawToolboxWindow,
                GUIContent.none,
                windowStyle
            );

            ConsumeToolboxGUIEvent();
        }

        private void DrawToolboxWindow(int windowId)
        {
            DrawRect(
                new Rect(0, 0, WindowWidth, 62),
                new Color(0.035f, 0.09f, 0.14f, 0.98f)
            );

            DrawRect(
                new Rect(0, 0, WindowWidth, 2),
                new Color(0.92f, 0.48f, 0.12f, 1f)
            );

            DrawRect(
                new Rect(0, 61, WindowWidth, 1),
                new Color(0.16f, 0.48f, 0.66f, 0.8f)
            );

            DrawRect(
                new Rect(16, 14, 4, 31),
                new Color(0.96f, 0.54f, 0.15f, 1f)
            );

            GUI.Label(
                new Rect(30, 10, 360, 30),
                "物 品 工 具 箱",
                titleStyle
            );

            GUI.Label(
                new Rect(31, 37, 430, 18),
                "ITEM FABRICATOR  //  LOCAL INVENTORY ACCESS",
                subtitleStyle
            );

            if (GUI.Button(
                new Rect(WindowWidth - 51, 15, 34, 30),
                "×",
                closeButtonStyle
            ))
            {
                CloseToolbox();
            }

            GUI.Label(
                new Rect(18, 78, 48, 24),
                "检索",
                smallLabelStyle
            );

            string newSearchText = GUI.TextField(
                new Rect(65, 76, 575, 28),
                searchText,
                inputStyle
            );

            if (newSearchText != searchText)
            {
                searchText = newSearchText;
                RefreshItems();
            }

            if (GUI.Button(
                new Rect(650, 76, 150, 28),
                "重新扫描数据库",
                buttonStyle
            ))
            {
                LoadItems();
            }

            GUI.Label(
                new Rect(18, 119, 48, 24),
                "数量",
                smallLabelStyle
            );

            amountText = GUI.TextField(
                new Rect(65, 117, 110, 28),
                amountText,
                inputStyle
            );

            if (GUI.Button(
                new Rect(188, 117, 54, 28),
                "1",
                compactButtonStyle
            ))
            {
                amountText = "1";
            }

            if (GUI.Button(
                new Rect(249, 117, 54, 28),
                "100",
                compactButtonStyle
            ))
            {
                amountText = "100";
            }

            if (GUI.Button(
                new Rect(310, 117, 60, 28),
                "1000",
                compactButtonStyle
            ))
            {
                amountText = "1000";
            }

            if (GUI.Button(
                new Rect(377, 117, 70, 28),
                "10000",
                compactButtonStyle
            ))
            {
                amountText = "10000";
            }

            GUI.Label(
                new Rect(480, 120, 310, 22),
                "当前结果：" + filteredItems.Count + " 项",
                labelStyle
            );

            DrawRect(
                new Rect(16, 158, WindowWidth - 32, 1),
                new Color(0.14f, 0.42f, 0.58f, 0.8f)
            );

            GUI.Label(
                new Rect(23, 165, 45, 22),
                "图标",
                smallLabelStyle
            );

            GUI.Label(
                new Rect(76, 165, 70, 22),
                "编号",
                smallLabelStyle
            );

            GUI.Label(
                new Rect(154, 165, 320, 22),
                "物品名称",
                smallLabelStyle
            );

            GUI.Label(
                new Rect(650, 165, 120, 22),
                "分类",
                smallLabelStyle
            );

            DrawRect(
                new Rect(16, 190, WindowWidth - 32, 330),
                new Color(0.018f, 0.042f, 0.065f, 0.88f)
            );

            DrawRect(
                new Rect(16, 190, WindowWidth - 32, 1),
                new Color(0.15f, 0.45f, 0.62f, 0.9f)
            );

            int contentHeight = Math.Max(filteredItems.Count * 34, 34);

            scrollPosition = GUI.BeginScrollView(
                new Rect(20, 194, WindowWidth - 42, 322),
                scrollPosition,
                new Rect(0, 0, WindowWidth - 68, contentHeight),
                false,
                true
            );

            if (filteredItems.Count == 0)
            {
                GUI.Label(
                    new Rect(18, 10, 600, 28),
                    "未检索到符合条件的物品。",
                    labelStyle
                );
            }

            for (int i = 0; i < filteredItems.Count; i++)
            {
                ItemProto item = filteredItems[i];

                float y = i * 34;
                bool isBuilding = item.prefabDesc != null;

                GUIStyle rowStyle = isBuilding
                    ? buildingItemRowStyle
                    : itemRowStyle;

                if (GUI.Button(
                    new Rect(0, y, WindowWidth - 92, 30),
                    "",
                    rowStyle
                ))
                {
                    GiveItem(item);
                }

                Color markColor = isBuilding
                    ? new Color(0.96f, 0.54f, 0.15f, 1f)
                    : new Color(0.18f, 0.68f, 0.86f, 1f);

                DrawRect(
                    new Rect(7, y + 7, 3, 16),
                    markColor
                );

                DrawItemIcon(
                    item,
                    new Rect(16, y + 3, 24, 24),
                    isBuilding
                );

                GUI.Label(
                    new Rect(50, y + 4, 65, 22),
                    "#" + item.ID,
                    idStyle
                );

                GUI.Label(
                    new Rect(122, y + 4, 460, 22),
                    item.name,
                    itemNameStyle
                );

                GUI.Label(
                    new Rect(600, y + 4, 135, 22),
                    isBuilding ? "建筑" : "物品",
                    tagStyle
                );
            }

            GUI.EndScrollView();

            DrawRect(
                new Rect(16, 534, WindowWidth - 32, 1),
                new Color(0.14f, 0.42f, 0.58f, 0.8f)
            );

            GUI.Label(
                new Rect(20, 544, 590, 28),
                message,
                footerStyle
            );

            if (GUI.Button(
                new Rect(WindowWidth - 144, 540, 124, 32),
                "关闭终端",
                buttonStyle
            ))
            {
                CloseToolbox();
            }

            GUI.DragWindow(
                new Rect(0, 0, WindowWidth - 58, 62)
            );
        }

        private Sprite GetItemIcon(ItemProto item)
        {
            if (item == null)
            {
                return null;
            }

            Sprite icon;

            if (itemIconCache.TryGetValue(item.ID, out icon))
            {
                return icon;
            }

            try
            {
                icon = Traverse.Create(item)
                    .Field("_iconSprite")
                    .GetValue<Sprite>();
            }
            catch
            {
                icon = null;
            }

            itemIconCache[item.ID] = icon;

            return icon;
        }

        private void DrawItemIcon(ItemProto item, Rect rect, bool isBuilding)
        {
            Color borderColor = isBuilding
                ? new Color(0.96f, 0.54f, 0.15f, 0.9f)
                : new Color(0.20f, 0.68f, 0.86f, 0.9f);

            DrawRect(
                new Rect(rect.x - 1, rect.y - 1, rect.width + 2, rect.height + 2),
                borderColor
            );

            DrawRect(
                rect,
                new Color(0.01f, 0.03f, 0.05f, 1f)
            );

            Sprite icon = GetItemIcon(item);

            if (icon != null && icon.texture != null)
            {
                Rect textureRect = icon.textureRect;

                Rect uv = new Rect(
                    textureRect.x / icon.texture.width,
                    textureRect.y / icon.texture.height,
                    textureRect.width / icon.texture.width,
                    textureRect.height / icon.texture.height
                );

                GUI.DrawTextureWithTexCoords(
                    rect,
                    icon.texture,
                    uv,
                    true
                );
            }
            else
            {
                GUI.Label(
                    rect,
                    isBuilding ? "建" : "?",
                    iconFallbackStyle
                );
            }
        }

        private void LockGameInput()
        {
            if (!inputLockSaved)
            {
                oldOnGUIOperate = GetVFInputBool("onGUIOperate");
                inputLockSaved = true;
            }

            SetVFInputBool("onGUIOperate", true);
        }

        private void RestoreGameInput()
        {
            if (!inputLockSaved)
            {
                return;
            }

            SetVFInputBool("onGUIOperate", oldOnGUIOperate);

            inputLockSaved = false;
        }

        private void ClearCameraInput()
        {
            ClearVFInputMember("_cameraZoomIn");
            ClearVFInputMember("_cameraZoomOut");
            ClearVFInputMember("cameraZoomIn");
            ClearVFInputMember("cameraZoomOut");

            ClearVFInputMember("mouseWheel");
            ClearVFInputMember("_mouseWheel");
            ClearVFInputMember("mouseScroll");
            ClearVFInputMember("scrollDelta");

            ClearVFInputMember("mouseMoveAxis");
            ClearVFInputMember("camJoystickAxis");
            ClearVFInputMember("cameraMoveAxis");
            ClearVFInputMember("cameraRotateAxis");

            TryConsumeVFInputMethod("UseMouseLeft");
            TryConsumeVFInputMethod("UseMouseRight");
            TryConsumeVFInputMethod("UseMouseMiddle");
            TryConsumeVFInputMethod("UseMouseWheel");
            TryConsumeVFInputMethod("UseMouseScroll");
        }

        private void EnsureVFInputType()
        {
            if (vfInputType != null)
            {
                return;
            }

            vfInputType = AccessTools.TypeByName("VFInput");
        }

        private MemberInfo GetVFInputMember(string name)
        {
            if (vfInputMemberCache.ContainsKey(name))
            {
                return vfInputMemberCache[name];
            }

            EnsureVFInputType();

            if (vfInputType == null)
            {
                vfInputMemberCache[name] = null;
                return null;
            }

            BindingFlags flags =
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static;

            MemberInfo member = vfInputType.GetField(name, flags);

            if (member == null)
            {
                member = vfInputType.GetProperty(name, flags);
            }

            vfInputMemberCache[name] = member;

            return member;
        }

        private bool GetVFInputBool(string name)
        {
            try
            {
                MemberInfo member = GetVFInputMember(name);

                FieldInfo field = member as FieldInfo;

                if (field != null && field.FieldType == typeof(bool))
                {
                    return (bool)field.GetValue(null);
                }

                PropertyInfo property = member as PropertyInfo;

                if (property != null &&
                    property.PropertyType == typeof(bool) &&
                    property.CanRead)
                {
                    return (bool)property.GetValue(null, null);
                }
            }
            catch
            {
            }

            return false;
        }

        private void SetVFInputBool(string name, bool value)
        {
            try
            {
                MemberInfo member = GetVFInputMember(name);

                FieldInfo field = member as FieldInfo;

                if (field != null && field.FieldType == typeof(bool))
                {
                    field.SetValue(null, value);
                    return;
                }

                PropertyInfo property = member as PropertyInfo;

                if (property != null &&
                    property.PropertyType == typeof(bool) &&
                    property.CanWrite)
                {
                    property.SetValue(null, value, null);
                }
            }
            catch
            {
            }
        }

        private void ClearVFInputMember(string name)
        {
            try
            {
                MemberInfo member = GetVFInputMember(name);

                if (member == null)
                {
                    return;
                }

                FieldInfo field = member as FieldInfo;

                if (field != null)
                {
                    if (field.FieldType.IsValueType)
                    {
                        field.SetValue(
                            null,
                            Activator.CreateInstance(field.FieldType)
                        );
                    }

                    return;
                }

                PropertyInfo property = member as PropertyInfo;

                if (property != null &&
                    property.CanWrite &&
                    property.PropertyType.IsValueType)
                {
                    property.SetValue(
                        null,
                        Activator.CreateInstance(property.PropertyType),
                        null
                    );
                }
            }
            catch
            {
            }
        }

        private void TryConsumeVFInputMethod(string methodName)
        {
            try
            {
                MethodInfo method;

                if (!vfInputMethodCache.TryGetValue(methodName, out method))
                {
                    EnsureVFInputType();

                    if (vfInputType != null)
                    {
                        method = vfInputType.GetMethod(
                            methodName,
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.Static,
                            null,
                            Type.EmptyTypes,
                            null
                        );
                    }

                    vfInputMethodCache[methodName] = method;
                }

                if (method != null)
                {
                    method.Invoke(null, null);
                }
            }
            catch
            {
            }
        }

        private void ConsumeToolboxGUIEvent()
        {
            Event currentEvent = Event.current;

            if (currentEvent == null ||
                currentEvent.type == EventType.Used)
            {
                return;
            }

            switch (currentEvent.type)
            {
                case EventType.KeyDown:
                case EventType.KeyUp:
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseDrag:
                case EventType.MouseMove:
                case EventType.ScrollWheel:
                case EventType.ContextClick:
                case EventType.ValidateCommand:
                case EventType.ExecuteCommand:
                    currentEvent.Use();
                    break;
            }
        }

        private void EnsureStyles()
        {
            if (stylesReady)
            {
                return;
            }

            stylesReady = true;

            whiteTexture = CreateTexture(Color.white);

            panelTexture = CreateTexture(
                new Color(0.025f, 0.06f, 0.09f, 0.98f)
            );

            headerTexture = CreateTexture(
                new Color(0.04f, 0.12f, 0.18f, 1f)
            );

            inputTexture = CreateTexture(
                new Color(0.025f, 0.075f, 0.11f, 1f)
            );

            buttonTexture = CreateTexture(
                new Color(0.08f, 0.18f, 0.25f, 1f)
            );

            buttonHoverTexture = CreateTexture(
                new Color(0.12f, 0.31f, 0.41f, 1f)
            );

            buttonActiveTexture = CreateTexture(
                new Color(0.55f, 0.28f, 0.08f, 1f)
            );

            itemTexture = CreateTexture(
                new Color(0.035f, 0.11f, 0.16f, 0.96f)
            );

            buildingItemTexture = CreateTexture(
                new Color(0.12f, 0.10f, 0.06f, 0.96f)
            );

            windowStyle = new GUIStyle(GUI.skin.box);
            windowStyle.normal.background = panelTexture;
            windowStyle.border = new RectOffset(1, 1, 1, 1);
            windowStyle.padding = new RectOffset(0, 0, 0, 0);

            titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontSize = 20;
            titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.normal.textColor =
                new Color(0.82f, 0.92f, 0.96f, 1f);

            subtitleStyle = new GUIStyle(GUI.skin.label);
            subtitleStyle.fontSize = 10;
            subtitleStyle.normal.textColor =
                new Color(0.35f, 0.67f, 0.82f, 1f);

            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 13;
            labelStyle.normal.textColor =
                new Color(0.72f, 0.84f, 0.9f, 1f);

            smallLabelStyle = new GUIStyle(GUI.skin.label);
            smallLabelStyle.fontSize = 12;
            smallLabelStyle.fontStyle = FontStyle.Bold;
            smallLabelStyle.normal.textColor =
                new Color(0.42f, 0.72f, 0.86f, 1f);

            inputStyle = new GUIStyle(GUI.skin.textField);
            inputStyle.fontSize = 13;
            inputStyle.normal.background = inputTexture;
            inputStyle.focused.background = inputTexture;
            inputStyle.hover.background = inputTexture;
            inputStyle.normal.textColor =
                new Color(0.88f, 0.95f, 0.98f, 1f);
            inputStyle.padding = new RectOffset(10, 8, 5, 4);

            buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.fontSize = 12;
            buttonStyle.fontStyle = FontStyle.Bold;
            buttonStyle.alignment = TextAnchor.MiddleCenter;
            buttonStyle.normal.background = buttonTexture;
            buttonStyle.hover.background = buttonHoverTexture;
            buttonStyle.active.background = buttonActiveTexture;
            buttonStyle.normal.textColor =
                new Color(0.8f, 0.91f, 0.96f, 1f);
            buttonStyle.hover.textColor = Color.white;
            buttonStyle.active.textColor = Color.white;

            compactButtonStyle = new GUIStyle(buttonStyle);
            compactButtonStyle.fontSize = 11;

            closeButtonStyle = new GUIStyle(buttonStyle);
            closeButtonStyle.fontSize = 20;
            closeButtonStyle.normal.background = headerTexture;
            closeButtonStyle.hover.background = buttonActiveTexture;

            itemRowStyle = new GUIStyle(GUI.skin.button);
            itemRowStyle.normal.background = itemTexture;
            itemRowStyle.hover.background = buttonHoverTexture;
            itemRowStyle.active.background = buttonActiveTexture;

            buildingItemRowStyle = new GUIStyle(GUI.skin.button);
            buildingItemRowStyle.normal.background = buildingItemTexture;
            buildingItemRowStyle.hover.background = buttonHoverTexture;
            buildingItemRowStyle.active.background = buttonActiveTexture;

            itemNameStyle = new GUIStyle(GUI.skin.label);
            itemNameStyle.fontSize = 13;
            itemNameStyle.alignment = TextAnchor.MiddleLeft;
            itemNameStyle.normal.textColor =
                new Color(0.87f, 0.93f, 0.96f, 1f);

            idStyle = new GUIStyle(GUI.skin.label);
            idStyle.fontSize = 11;
            idStyle.alignment = TextAnchor.MiddleLeft;
            idStyle.normal.textColor =
                new Color(0.37f, 0.67f, 0.82f, 1f);

            tagStyle = new GUIStyle(GUI.skin.label);
            tagStyle.fontSize = 11;
            tagStyle.alignment = TextAnchor.MiddleCenter;
            tagStyle.normal.textColor =
                new Color(0.95f, 0.69f, 0.32f, 1f);

            footerStyle = new GUIStyle(GUI.skin.label);
            footerStyle.fontSize = 12;
            footerStyle.normal.textColor =
                new Color(0.54f, 0.72f, 0.8f, 1f);

            iconFallbackStyle = new GUIStyle(GUI.skin.label);
            iconFallbackStyle.fontSize = 11;
            iconFallbackStyle.fontStyle = FontStyle.Bold;
            iconFallbackStyle.alignment = TextAnchor.MiddleCenter;
            iconFallbackStyle.normal.textColor =
                new Color(0.85f, 0.92f, 0.96f, 1f);
        }

        private Texture2D CreateTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1);

            texture.SetPixel(0, 0, color);
            texture.Apply();

            return texture;
        }

        private void DrawRect(Rect rect, Color color)
        {
            Color oldColor = GUI.color;

            GUI.color = color;
            GUI.DrawTexture(rect, whiteTexture);

            GUI.color = oldColor;
        }
    }
}