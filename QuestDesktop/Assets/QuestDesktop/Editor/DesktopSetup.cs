using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace QuestDesktop.EditorTools
{
    /// <summary>
    /// Меню Desktop: настройка проекта под Quest 3, создание сцены и сборка APK.
    /// Используемые API сверены с Meta XR Core SDK 209.0.0, OpenXR 1.16.1, XR Management 4.6.1.
    /// Те же шаги доступны из командной строки: -executeMethod QuestDesktop.EditorTools.DesktopSetup.BatchSetup
    /// </summary>
    public static class DesktopSetup
    {
        const string ScenePath = "Assets/Scenes/Desktop.unity";
        const string ApkPath = "Builds/QuestDesktop.apk";
        const string AppId = "com.miandic.questdesktop";
        const string MetaXRFeatureSetId = "com.meta.openxr.featureset.metaxr"; // Meta.XR.MetaXRFeatureSet.featureSetId
        const string OpenXRLoaderType = "UnityEngine.XR.OpenXR.OpenXRLoader";
        const string XRSettingsAssetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";

        [MenuItem("Desktop/1. Настроить проект для Quest 3", priority = 1)]
        public static void ConfigureProject()
        {
            var log = new StringBuilder("[Desktop] Настройка проекта:\n");
            var android = NamedBuildTarget.Android;

            PlayerSettings.companyName = "Miandic";
            PlayerSettings.productName = "Quest Desktop";
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.SetApplicationIdentifier(android, AppId);
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Minimal);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.runInBackground = true;
            log.AppendLine($"  Player: {AppId}, IL2CPP, ARM64, minSdk 32, Vulkan, Linear, INTERNET");

            ConfigureXRLoader(log);
            ConfigureOpenXRFeatures(log);
            ConfigureOvr(log);

            AssetDatabase.SaveAssets();
            log.AppendLine("Проверьте Meta > Tools > Project Setup Tool: обязательные (Required) пункты должны быть выполнены.");
            Debug.Log(log.ToString());
        }

        static void ConfigureXRLoader(StringBuilder log)
        {
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget);
            if (perTarget == null)
            {
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                Directory.CreateDirectory(Path.GetDirectoryName(XRSettingsAssetPath));
                AssetDatabase.CreateAsset(perTarget, XRSettingsAssetPath);
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            }
            if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Android))
                perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
                perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);

            var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
            settings.InitManagerOnStart = true;
            bool ok = XRPackageMetadataStore.AssignLoader(settings.Manager, OpenXRLoaderType, BuildTargetGroup.Android);
            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(perTarget);
            log.AppendLine(ok || settings.Manager.activeLoaders.Any(l => l != null && l.GetType().FullName == OpenXRLoaderType)
                ? "  XR Plug-in Management (Android): OpenXR"
                : "  ! Не удалось назначить OpenXR loader для Android — включите вручную в XR Plug-in Management");
        }

        static void ConfigureOpenXRFeatures(StringBuilder log)
        {
            var featureSet = OpenXRFeatureSetManager.GetFeatureSetWithId(BuildTargetGroup.Android, MetaXRFeatureSetId);
            if (featureSet != null)
            {
                featureSet.isEnabled = true;
                OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(BuildTargetGroup.Android);
                log.AppendLine("  OpenXR (Android): группа Meta XR включена");
            }
            else
            {
                log.AppendLine("  ! Группа функций Meta XR не найдена в OpenXR (Android)");
            }

            var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (openXr != null)
            {
                foreach (var feature in openXr.GetFeatures<OpenXRFeature>())
                {
                    if (feature == null) continue;
                    if (feature.GetType().Name == "HandTracking" || feature.GetType().Name == "MetaHandTrackingAim")
                        feature.enabled = true;
                    if (feature is UnityEngine.XR.OpenXR.Features.Interactions.OculusTouchControllerProfile)
                        feature.enabled = true;
                    EditorUtility.SetDirty(feature);
                }
            }
            var quest = openXr != null ? openXr.GetFeature<MetaQuestFeature>() : null;
            if (quest != null)
            {
                quest.enabled = true;
                // Приложению нужен INTERNET для TLS-подключения к ПК.
                quest.ForceRemoveInternetPermission = false;
                EditorUtility.SetDirty(quest);
                log.AppendLine("  OpenXR (Android): Meta Quest Support включён, ForceRemoveInternetPermission = false");
            }
            else
            {
                log.AppendLine("  ! MetaQuestFeature не найден в настройках OpenXR (Android)");
            }
        }

        static void ConfigureOvr(StringBuilder log)
        {
            var cfg = OVRProjectConfig.CachedProjectConfig;
            if (cfg == null)
            {
                log.AppendLine("  ! OVRProjectConfig недоступен — повторите команду после окончания импорта");
            }
            else
            {
                cfg.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.HandsOnly;
                cfg.handTrackingFrequency = OVRProjectConfig.HandTrackingFrequency.HIGH;
                cfg.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
                OVRProjectConfig.CommitProjectConfig(cfg);
                log.AppendLine("  OVRProjectConfig: Hands Only, HIGH, passthrough Required");
            }
            var runtime = OVRRuntimeSettings.GetRuntimeSettings();
            runtime.HandSkeletonVersion = OVRHandSkeletonVersion.OpenXR;
            OVRRuntimeSettings.CommitRuntimeSettings(runtime);
        }

        [MenuItem("Desktop/2. Создать сцену", priority = 2)]
        public static void BuildScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var log = new StringBuilder("[Desktop] Создание сцены:\n");

            string rigPath = AssetDatabase.FindAssets("OVRCameraRig t:Prefab", new[] { "Packages", "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == "OVRCameraRig");
            if (rigPath == null)
            {
                Debug.LogError("[Desktop] Префаб OVRCameraRig не найден. Установлен ли com.meta.xr.sdk.core?");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(rigPath), scene);
            rig.name = "OVRCameraRig";
            log.AppendLine("  OVRCameraRig: " + rigPath);

            var mgr = rig.GetComponent<OVRManager>();
            if (mgr == null) mgr = rig.AddComponent<OVRManager>();
            mgr.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            mgr.isInsightPassthroughEnabled = true;
            log.AppendLine("  OVRManager: Floor Level, passthrough включён");

            // OVRPassthroughLayer сам ставит себя подложкой (Underlay) за сценой.
            if (rig.GetComponent<OVRPassthroughLayer>() == null) rig.AddComponent<OVRPassthroughLayer>();

            foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
            }
            log.AppendLine("  Камеры: прозрачный фон для passthrough");

            new GameObject("DesktopApp").AddComponent<DesktopApp>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            log.AppendLine("  Сохранено: " + ScenePath + " (добавлено в Build Settings)");
            Debug.Log(log.ToString());
        }

        [MenuItem("Desktop/3. Собрать APK", priority = 3)]
        public static void BuildApk()
        {
            EditorUserBuildSettings.selectedBuildTargetGroup = BuildTargetGroup.Android;
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android
                && !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                Debug.LogError("[Desktop] Не удалось переключиться на Android. Установлен ли модуль Android Build Support?");
                return;
            }
            ConfigureProject();
            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.Development
            });
            Debug.Log($"[Desktop] Сборка: {report.summary.result}, {report.summary.totalSize / 1048576.0:F1} МБ, " +
                      $"{report.summary.totalErrors} ошибок, {report.summary.outputPath}");
            if (Application.isBatchMode && report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }

        /// <summary>Для командной строки: настройка + сцена (без сборки).</summary>
        public static void BatchSetup()
        {
            ConfigureProject();
            BuildScene();
            AssetDatabase.SaveAssets();
        }
    }
}


