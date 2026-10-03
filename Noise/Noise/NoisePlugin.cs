using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.IO;
using System;
using System.Reflection;
using TMPro;

namespace Noise;

[BepInPlugin("denyscrasav4ik.thedumbfactory.noise", "Noise", "1.0.1")]
public class NoisePlugin : BaseUnityPlugin
{
    public static Shader noiseShader;

    public static SoundObject noiseJumpscare;
    public static SoundObject noiseFastForward;
    public static SoundObject noiseStep;
    public static SoundObject noiseIdle;
    public static SoundObject noiseStop;
    public static SoundObject noiseThreat1;
    public static SoundObject noiseThreat2;

    public static TMP_FontAsset noiseFont;

    public static TMP_SpriteAsset noisePlayButton;
    public static TMP_SpriteAsset noiseStopButton;
    public static TMP_SpriteAsset noiseFastforwardButton;

    public static Texture2D[] staticFrames = new Texture2D[30];

    public static ConfigEntry<float> noiseSpeedConfig;
    public static ConfigEntry<float> noiseFastForwardSpeedConfig;
    public static ConfigEntry<float> noiseFastForwardDistanceConfig;

    private void Awake()
    {
        noiseSpeedConfig = Config.Bind("General", "Noise Speed", 10f, "The normal follow speed of Noise.");
        noiseFastForwardSpeedConfig = Config.Bind("General", "Noise Fast Forward Speed", 28f, "The speed of Noise when fast forwarding.");
        noiseFastForwardDistanceConfig = Config.Bind("General", "Noise Fast Forward Distance", 5f, "The distance (in tiles) required between the player and Noise to trigger fast forward.");

        LoadAssets();
        GenerateStaticFrames();
        new Harmony("denyscrasav4ik.thedumbfactory.noise").PatchAll();
    }

    private void GenerateStaticFrames()
    {
        for (int i = 0; i < 30; i++)
        {
            Texture2D tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            Color[] pixels = new Color[256 * 256];

            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    float u = (x / 255f) * 2f - 1f;
                    float v = (y / 255f) * 2f - 1f;
                    float dist = Mathf.Clamp01(new Vector2(u, v).magnitude);

                    float alpha = Mathf.Pow(dist, 2.5f);
                    float staticVal = UnityEngine.Random.value;

                    pixels[y * 256 + x] = new Color(staticVal, staticVal, staticVal, alpha);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            staticFrames[i] = tex;
        }
    }

    private void LoadAssets()
    {
        string bundleName =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Noise.Resources.assets-win.bundle" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Noise.Resources.assets-mac.bundle" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Noise.Resources.assets-linux.bundle" :
            throw new PlatformNotSupportedException();

        using Stream stream = typeof(NoisePlugin).Assembly.GetManifestResourceStream(bundleName)!;
        AssetBundle bundle = AssetBundle.LoadFromStream(stream);

        noiseShader = bundle.LoadAsset<Shader>("Noise_TVStatic");

        noiseJumpscare = bundle.LoadAsset<SoundObject>("Noise_Death");
        noiseFastForward = bundle.LoadAsset<SoundObject>("Noise_Fast");
        noiseStep = bundle.LoadAsset<SoundObject>("Noise_Footsteps");
        noiseIdle = bundle.LoadAsset<SoundObject>("Noise_Idle");
        noiseStop = bundle.LoadAsset<SoundObject>("Noise_Pause");
        noiseThreat1 = bundle.LoadAsset<SoundObject>("Noise_Threat1");
        noiseThreat2 = bundle.LoadAsset<SoundObject>("Noise_Threat2");

        noiseFont = bundle.LoadAsset<TMP_FontAsset>("Noise_VCROSDFont");

        noisePlayButton = bundle.LoadAsset<TMP_SpriteAsset>("Noise_PlayButton");
        noiseStopButton = bundle.LoadAsset<TMP_SpriteAsset>("Noise_PauseButton");
        noiseFastforwardButton = bundle.LoadAsset<TMP_SpriteAsset>("Noise_FastforwardButton");

        bundle.Unload(false);
    }
}

[HarmonyPatch(typeof(BaseGameManager), "BeginSpoopMode")]
public class BaseGameManager_BeginSpoopMode_Patch
{
    public static bool SpoopModeStarted { get; private set; } = false;

    public static void Postfix() => SpoopModeStarted = true;
    public static void Reset() => SpoopModeStarted = false;
}

[HarmonyPatch(typeof(PlayerManager), "Start")]
public class PlayerManager_Start_Patch
{
    public static void Postfix(PlayerManager __instance)
    {
        BaseGameManager_BeginSpoopMode_Patch.Reset();
        __instance.gameObject.AddComponent<NoiseBehavior>();
    }
}

public class NoiseBehavior : MonoBehaviour
{
    private GameObject noiseBody;
    private Animator noiseAnimator;
    private SpriteRenderer[] renderers;
    private PlayerManager pm;

    private List<Vector3> positionHistory = new List<Vector3>();
    private Vector3 lastPlayerPosition;

    private AudioManager audMan;
    private bool wasMoving = false;
    private bool wasFast = false;
    private float distanceMoved = 0f;
    private Vector3 lastFrameNoisePos;

    private GameObject vignetteOverlay;
    private UnityEngine.UI.RawImage vignetteImage;
    private AudioManager threatAudMan;
    private int currentThreatLevel = 0;

    private GameObject currentTexts;
    private float ffVfxTimer = 0f;

    private const float Threat1Distance = 40f;
    private const float Threat2Distance = 20f;
    private const float CollisionRadius = 3f;

    private float CatchUpDistance => NoisePlugin.noiseFastForwardDistanceConfig.Value;
    private float FollowSpeed => NoisePlugin.noiseSpeedConfig.Value;
    private float CatchUpSpeed => NoisePlugin.noiseFastForwardSpeedConfig.Value;

    private void Awake()
    {
        vignetteOverlay = new GameObject("VignetteOverlay");
        Canvas canvas = vignetteOverlay.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 99;
        vignetteOverlay.AddComponent<UnityEngine.UI.CanvasScaler>().uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;

        GameObject imageObj = new GameObject("VignetteImage");
        imageObj.transform.SetParent(vignetteOverlay.transform, false);
        vignetteImage = imageObj.AddComponent<UnityEngine.UI.RawImage>();
        vignetteImage.rectTransform.anchorMin = Vector2.zero;
        vignetteImage.rectTransform.anchorMax = Vector2.one;
        vignetteImage.rectTransform.offsetMin = Vector2.zero;
        vignetteImage.rectTransform.offsetMax = Vector2.zero;
        vignetteImage.color = new Color(1f, 1f, 1f, 0f);

        GameObject threatAudioObj = new GameObject("ThreatAudio");
        threatAudioObj.transform.SetParent(transform);
        threatAudioObj.SetActive(false);

        AudioSource src = threatAudioObj.AddComponent<AudioSource>();
        threatAudMan = threatAudioObj.AddComponent<AudioManager>();
        threatAudMan.audioDevice = src;
        threatAudMan.ignoreListenerPause = true;

        threatAudioObj.SetActive(true);
    }

    private void Start()
    {
        pm = GetComponent<PlayerManager>();
        if (pm != null)
            lastPlayerPosition = pm.transform.position;
        CreateNoiseBody();
    }

    private void CreateNoiseBody()
    {
        Student[] studentPrefabs = Resources.FindObjectsOfTypeAll<Student>();
        if (studentPrefabs != null && studentPrefabs.Length != 0)
        {
            noiseBody = UnityEngine.Object.Instantiate(studentPrefabs[0].gameObject);
            noiseBody.name = "Noise_Entity";
            noiseBody.transform.position = pm.transform.position;

            Collider[] colliders = noiseBody.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                colliders[i].enabled = false;
            Rigidbody[] rigidbodies = noiseBody.GetComponentsInChildren<Rigidbody>(true);
            foreach (Rigidbody rb in rigidbodies)
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }

            if (noiseBody.GetComponent<NPC>() != null) noiseBody.GetComponent<NPC>().enabled = false;
            if (noiseBody.GetComponent<Entity>() != null) noiseBody.GetComponent<Entity>().enabled = false;
            if (noiseBody.GetComponent<Navigator>() != null) noiseBody.GetComponent<Navigator>().enabled = false;
            if (noiseBody.GetComponent<ActivityModifier>() != null) noiseBody.GetComponent<ActivityModifier>().enabled = false;
            if (noiseBody.GetComponent<Looker>() != null) noiseBody.GetComponent<Looker>().enabled = false;

            noiseAnimator = noiseBody.GetComponentInChildren<Animator>(true);
            if (noiseAnimator != null)
                noiseAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            renderers = noiseBody.GetComponentsInChildren<SpriteRenderer>(true);
            if (NoisePlugin.noiseShader != null && renderers != null)
            {
                foreach (SpriteRenderer sr in renderers)
                {
                    if (sr != null)
                    {
                        sr.material = new Material(NoisePlugin.noiseShader);

                        if (sr.gameObject.GetComponent<BillboardUpdater>() == null)
                            sr.gameObject.AddComponent<BillboardUpdater>();
                    }
                }
            }

            audMan = noiseBody.GetComponentInChildren<AudioManager>(true);

            if (audMan == null)
            {
                noiseBody.SetActive(false);
                AudioSource newSrc = noiseBody.AddComponent<AudioSource>();
                audMan = noiseBody.AddComponent<AudioManager>();
                audMan.audioDevice = newSrc;
            }

            noiseBody.SetActive(false);
        }
    }

    private void Update()
    {
        if (noiseBody == null || pm == null) return;

        if (vignetteImage != null && NoisePlugin.staticFrames.Length > 0)
        {
            int frameIndex = (int)(Time.time * 30f) % 30;
            vignetteImage.texture = NoisePlugin.staticFrames[frameIndex];
        }

        bool isPlayerMoving = Vector3.Distance(pm.transform.position, lastPlayerPosition) > 0.001f;
        lastPlayerPosition = pm.transform.position;

        if (isPlayerMoving)
        {
            if (positionHistory.Count == 0 || Vector3.Distance(positionHistory.Last(), pm.transform.position) > 0.5f)
                positionHistory.Add(pm.transform.position);
        }

        if (!BaseGameManager_BeginSpoopMode_Patch.SpoopModeStarted)
        {
            if (noiseBody.activeSelf)
            {
                if (audMan != null) audMan.FlushQueue(true);
                noiseBody.SetActive(false);
            }

            if (vignetteImage != null) vignetteImage.color = new Color(1f, 1f, 1f, 0f);
            if (currentThreatLevel != 0 && threatAudMan != null)
            {
                threatAudMan.FlushQueue(true);
                currentThreatLevel = 0;
            }
            if (currentTexts != null) Destroy(currentTexts);
            return;
        }

        if (!noiseBody.activeSelf)
        {
            noiseBody.SetActive(true);

            int spawnIndex = 0;
            for (int i = positionHistory.Count - 1; i >= 0; i--)
            {
                if (Vector3.Distance(positionHistory[i], pm.transform.position) >= 50f)
                {
                    spawnIndex = i;
                    break;
                }
            }

            if (positionHistory.Count > 0)
            {
                noiseBody.transform.position = positionHistory[spawnIndex];
                positionHistory.RemoveRange(0, spawnIndex);
            }
            else
                noiseBody.transform.position = pm.transform.position;

            lastFrameNoisePos = noiseBody.transform.position;
        }

        float distanceToPlayer = Vector3.Distance(noiseBody.transform.position, pm.transform.position);

        if (distanceToPlayer < CollisionRadius)
        {
            GameObject tempAudioObj = new GameObject("JumpscareTempAudio");
            tempAudioObj.SetActive(false);
            tempAudioObj.transform.position = noiseBody.transform.position;

            AudioSource tempSrc = tempAudioObj.AddComponent<AudioSource>();
            AudioManager tempAud = tempAudioObj.AddComponent<AudioManager>();
            tempAud.audioDevice = tempSrc;

            tempAudioObj.SetActive(true);
            tempAud.ignoreListenerPause = true;
            tempAud.useUnscaledPitch = true;
            tempAud.PlaySingle(NoisePlugin.noiseJumpscare);
            Destroy(tempAudioObj, 10f);

            StartCoroutine(CustomEndSequence());

            enabled = false;
            return;
        }

        bool isNoiseMoving = false;
        float moveSpeed = (distanceToPlayer >= CatchUpDistance * 10f) ? CatchUpSpeed : FollowSpeed;
        bool isFast = (moveSpeed == CatchUpSpeed);

        if (distanceToPlayer >= CatchUpDistance * 10f)
            isNoiseMoving = positionHistory.Count > 0 || Vector3.Distance(noiseBody.transform.position, pm.transform.position) > 0.1f;
        else if (isPlayerMoving && positionHistory.Count > 0)
            isNoiseMoving = true;

        if (isNoiseMoving)
        {
            Vector3 targetPosition = (positionHistory.Count > 0) ? positionHistory[0] : pm.transform.position;

            Vector3 moveDir = targetPosition - noiseBody.transform.position;
            moveDir.y = 0f;

            if (moveDir.sqrMagnitude > 0.001f)
                noiseBody.transform.rotation = Quaternion.LookRotation(moveDir);

            noiseBody.transform.position = Vector3.MoveTowards(noiseBody.transform.position, targetPosition, moveSpeed * Time.deltaTime);

            if (positionHistory.Count > 0 && Vector3.Distance(noiseBody.transform.position, positionHistory[0]) < 0.2f)
                positionHistory.RemoveAt(0);
        }

        UpdateAnimation(isNoiseMoving, moveSpeed);
        UpdateAudio(isNoiseMoving, isFast);

        bool isPaused = !isNoiseMoving;
        UpdateThreatLevel(distanceToPlayer, isPaused);

        if (renderers != null)
        {
            foreach (SpriteRenderer sr in renderers)
            {
                if (sr != null && sr.material != null)
                {
                    sr.material.SetFloat("_IsPaused", isPaused ? 1f : 0f);
                    sr.material.SetFloat("_IsFastForward", (isNoiseMoving && isFast) ? 1f : 0f);
                }
            }
        }

        if (!isNoiseMoving && wasMoving)
        {
            if (currentTexts != null) Destroy(currentTexts);
            currentTexts = SpawnText("Noise_Vfx_Pause", NoisePlugin.noiseStopButton, 0f, true);
        }
        else if (isNoiseMoving && !wasMoving)
        {
            if (currentTexts != null) Destroy(currentTexts);
            currentTexts = SpawnText("Noise_Vfx_Play", NoisePlugin.noisePlayButton, 1f, false);
        }

        if (isNoiseMoving && isFast)
        {
            ffVfxTimer -= Time.deltaTime;
            if (ffVfxTimer <= 0f)
            {
                if (currentTexts != null) Destroy(currentTexts);
                currentTexts = SpawnText("Noise_Vfx_Fastforward", NoisePlugin.noiseFastforwardButton, 0.1f, false);
                ffVfxTimer = 0.1f;
            }
        }
        else ffVfxTimer = 0f;

        wasMoving = isNoiseMoving;
        wasFast = isFast;
    }

    private GameObject SpawnText(string textKey, TMP_SpriteAsset spriteAsset, float lifetime, bool isPaused)
    {
        GameObject vhs = new GameObject("VHS_" + textKey);
        vhs.transform.SetParent(noiseBody.transform, true);

        int targetLayer = 0;
        int baseOrder = 0;
        if (renderers != null && renderers.Length > 0)
        {
            targetLayer = renderers[0].gameObject.layer;
            baseOrder = renderers[0].sortingOrder;
        }

        vhs.layer = targetLayer;

        Vector3 offset = new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), UnityEngine.Random.Range(-3.0f, -1.0f), UnityEngine.Random.Range(-1.5f, 1.5f));
        vhs.transform.position = noiseBody.transform.position + offset;

        TextMeshPro tmp = vhs.AddComponent<TextMeshPro>();

        if (NoisePlugin.noiseFont != null) tmp.font = NoisePlugin.noiseFont;
        if (spriteAsset != null) tmp.spriteAsset = spriteAsset;

        tmp.text = Singleton<LocalizationManager>.Instance.GetLocalizedText(textKey);
        tmp.fontSize = 10f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = isPaused ? Color.black : Color.white;

        if (renderers != null && renderers.Length > 0)
            tmp.sortingLayerID = renderers[0].sortingLayerID;
        tmp.sortingOrder = baseOrder + 1;

        Material textMat = new Material(tmp.fontMaterial);
        tmp.fontMaterial = textMat;

        vhs.AddComponent<BillboardUpdater>();

        if (isPaused)
        {
            GameObject blueVhs = new GameObject("VHS_" + textKey + "_Blue");

            blueVhs.layer = targetLayer;

            blueVhs.transform.SetParent(vhs.transform, false);
            blueVhs.transform.localPosition = Vector3.zero;
            blueVhs.transform.localRotation = Quaternion.identity;

            TextMeshPro tmpBlue = blueVhs.AddComponent<TextMeshPro>();
            if (NoisePlugin.noiseFont != null) tmpBlue.font = NoisePlugin.noiseFont;
            if (spriteAsset != null) tmpBlue.spriteAsset = spriteAsset;

            tmpBlue.text = tmp.text;
            tmpBlue.fontSize = tmp.fontSize;
            tmpBlue.alignment = tmp.alignment;
            tmpBlue.color = Color.blue;

            tmpBlue.sortingLayerID = tmp.sortingLayerID;
            tmpBlue.sortingOrder = tmp.sortingOrder + 1;

            Material blueMat = new Material(tmpBlue.fontMaterial);
            blueMat.SetFloat("_Stencil", 1);
            blueMat.SetFloat("_StencilComp", (float)UnityEngine.Rendering.CompareFunction.Equal);
            tmpBlue.fontMaterial = blueMat;
        }

        if (lifetime > 0f)
            Destroy(vhs, lifetime);


        return vhs;
    }

    private IEnumerator CustomEndSequence()
    {
        if (Singleton<MusicManager>.Instance != null)
            Singleton<MusicManager>.Instance.StopMidi();


        CoreGameManager cgm = Singleton<CoreGameManager>.Instance;

        if (cgm != null)
        {
            cgm.disablePause = true;
            Time.timeScale = 0f;

            GameCamera cam = cgm.GetCamera(0);
            if (cam != null)
            {
                cam.SetControllable(false);
                cam.matchTargetRotation = false;
                cam.UpdateTargets(noiseBody.transform, 0);
                cam.offestPos = (pm.transform.position - noiseBody.transform.position).normalized * 2f + (Vector3.up * 0.5f);
            }

            if (Singleton<StickerManager>.Instance != null)
                Singleton<StickerManager>.Instance.RemoveUnopenedStickerPacketsFromInventory();

            if (Singleton<InputManager>.Instance != null)
                Singleton<InputManager>.Instance.Rumble(1f, 2f);

            if (Singleton<HighlightManager>.Instance != null && Singleton<BaseGameManager>.Instance != null && cgm.sceneObject != null)
            {
                Singleton<HighlightManager>.Instance.Highlight(
                    "steam_x",
                    Singleton<LocalizationManager>.Instance.GetLocalizedText("Steam_Highlight_Lose"),
                    string.Format(
                        Singleton<LocalizationManager>.Instance.GetLocalizedText("Steam_Highlight_Lose_Desc"),
                        Singleton<LocalizationManager>.Instance.GetLocalizedText(Singleton<BaseGameManager>.Instance.managerNameKey),
                        Singleton<LocalizationManager>.Instance.GetLocalizedText(cgm.sceneObject.nameKey)
                    ),
                    2u,
                    0f,
                    0f,
                    TimelineEventClipPriority.Standard
                );
            }
        }

        StartCoroutine(CameraShake(6f, 0.35f));

        yield return new WaitForSecondsRealtime(2f);

        GameObject vhsOverlay = new GameObject("VHSOverlay");
        AudioListener.volume = 0f;
        UnityEngine.Object.DontDestroyOnLoad(vhsOverlay);
        Canvas canvas = vhsOverlay.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;

        GameObject imageObj = new GameObject("VHSBackground");
        imageObj.transform.SetParent(vhsOverlay.transform, false);
        UnityEngine.UI.Image img = imageObj.AddComponent<UnityEngine.UI.Image>();
        img.rectTransform.anchorMin = Vector2.zero;
        img.rectTransform.anchorMax = Vector2.one;
        img.rectTransform.offsetMin = Vector2.zero;
        img.rectTransform.offsetMax = Vector2.zero;

        Color overlayColor;
        ColorUtility.TryParseHtmlString("#020018", out overlayColor);
        img.color = overlayColor;

        GameObject textOverlay = new GameObject("VHSTextOverlay");
        UnityEngine.Object.DontDestroyOnLoad(textOverlay);
        Canvas textCanvas = textOverlay.AddComponent<Canvas>();
        textCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        textCanvas.sortingOrder = 1001;

        GameObject textObj = new GameObject("VHSText");
        textObj.transform.SetParent(textOverlay.transform, false);

        TextMeshProUGUI vhsText = textObj.AddComponent<TextMeshProUGUI>();

        if (NoisePlugin.noiseFont != null)
        {
            vhsText.font = NoisePlugin.noiseFont;
            vhsText.fontMaterial = new Material(NoisePlugin.noiseFont.material);
            vhsText.fontMaterial.renderQueue = 3000;
        }

        if (NoisePlugin.noiseStopButton != null)
        {
            vhsText.spriteAsset = NoisePlugin.noiseStopButton;
        }

        vhsText.fontSize = 100f;
        vhsText.color = Color.white;
        vhsText.alignment = TextAlignmentOptions.BottomLeft;
        vhsText.richText = true;

        RectTransform textRect = vhsText.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.zero;
        textRect.pivot = Vector2.zero;
        textRect.anchoredPosition = new Vector2(20f, 20f);
        textRect.sizeDelta = new Vector2(500f, 100f);

        vhsText.text = Singleton<LocalizationManager>.Instance.GetLocalizedText("Noise_Vfx_Pause");

        if (cgm != null)
        {
            FieldInfo livesField = HarmonyLib.AccessTools.Field(typeof(CoreGameManager), "lives");
            FieldInfo extraLivesField = HarmonyLib.AccessTools.Field(typeof(CoreGameManager), "extraLives");
            FieldInfo attemptsField = HarmonyLib.AccessTools.Field(typeof(CoreGameManager), "attempts");

            int lives = (int)livesField.GetValue(cgm);
            int extraLives = (int)extraLivesField.GetValue(cgm);
            int attempts = (int)attemptsField.GetValue(cgm);

            if (lives < 1 && extraLives < 1)
            {
                if (vhsOverlay != null) Destroy(vhsOverlay);
                if (textOverlay != null) Destroy(textOverlay);
                AudioListener.volume = 1f;
                if (Singleton<GlobalCam>.Instance != null)
                    Singleton<GlobalCam>.Instance.SetListener(val: true);
                cgm.ReturnToMenu();
                yield break;
            }

            if (lives > 0)
            {
                lives--;
                attempts++;
                livesField.SetValue(cgm, lives);
                attemptsField.SetValue(cgm, attempts);
            }
            else
            {
                extraLives--;
                extraLivesField.SetValue(cgm, extraLives);
            }
        }

        if (Singleton<BaseGameManager>.Instance != null)
        {
            if (cgm != null)
                cgm.StartCoroutine(FinishEndSequence(vhsOverlay, textOverlay, vhsText));

            Singleton<BaseGameManager>.Instance.RestartLevel();
        }
    }

    private IEnumerator FinishEndSequence(GameObject blueScreen, GameObject textScreen, TextMeshProUGUI vhsText)
    {
        yield return new WaitForSecondsRealtime(4f);

        if (blueScreen != null) Destroy(blueScreen);

        AudioListener.volume = 1f;

        if (vhsText != null)
        {
            if (NoisePlugin.noisePlayButton != null)
                vhsText.spriteAsset = NoisePlugin.noisePlayButton;
            vhsText.text = Singleton<LocalizationManager>.Instance.GetLocalizedText("Noise_Vfx_Play");
        }

        yield return new WaitForSecondsRealtime(3f);

        if (textScreen != null) Destroy(textScreen);
    }

    private IEnumerator CameraShake(float duration, float intensity)
    {
        if (Singleton<CoreGameManager>.Instance == null) yield break;
        GameCamera cam = Singleton<CoreGameManager>.Instance.GetCamera(0);
        if (cam == null) yield break;

        Vector3 baseOffset = cam.offestPos;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            Vector3 shakeOffset = UnityEngine.Random.insideUnitSphere * intensity;
            cam.offestPos = baseOffset + shakeOffset;
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        cam.offestPos = baseOffset;
    }

    private void UpdateThreatLevel(float distanceToPlayer, bool isPaused)
    {
        float vignetteAlpha = 0f;
        int newThreatLevel = 0;

        if (distanceToPlayer < Threat2Distance)
        {
            newThreatLevel = 2;
            vignetteAlpha = Mathf.Lerp(1f, 0.5f, (distanceToPlayer - CollisionRadius) / (Threat2Distance - CollisionRadius));
        }
        else if (distanceToPlayer < Threat1Distance)
        {
            newThreatLevel = 1;
            vignetteAlpha = Mathf.Lerp(0.5f, 0f, (distanceToPlayer - Threat2Distance) / (Threat1Distance - Threat2Distance));
        }

        if (vignetteImage != null)
        {
            float colorVal = isPaused ? 0f : 1f;
            vignetteImage.color = new Color(colorVal, colorVal, colorVal, vignetteAlpha);
        }

        if (threatAudMan != null)
        {
            threatAudMan.volumeModifier = vignetteAlpha;
            if (threatAudMan.audioSourceManager != null)
                threatAudMan.audioSourceManager.volume = vignetteAlpha;
        }

        if (newThreatLevel != currentThreatLevel)
        {
            currentThreatLevel = newThreatLevel;
            if (threatAudMan != null)
            {
                threatAudMan.FlushQueue(true);
                if (currentThreatLevel == 1 && NoisePlugin.noiseThreat1 != null)
                {
                    threatAudMan.QueueAudio(NoisePlugin.noiseThreat1);
                    threatAudMan.SetLoop(true);
                }
                else if (currentThreatLevel == 2 && NoisePlugin.noiseThreat2 != null)
                {
                    threatAudMan.QueueAudio(NoisePlugin.noiseThreat2);
                    threatAudMan.SetLoop(true);
                }
            }
        }
    }

    private void UpdateAudio(bool isNoiseMoving, bool isFast)
    {
        if (audMan == null) return;

        if (isNoiseMoving != wasMoving)
        {
            audMan.FlushQueue(true);

            if (isNoiseMoving)
            {
                audMan.QueueAudio(isFast ? NoisePlugin.noiseFastForward : NoisePlugin.noiseIdle);
                audMan.SetLoop(true);
            }
            else
            {
                audMan.PlaySingle(NoisePlugin.noiseStop);
            }
        }
        else if (isNoiseMoving && isFast != wasFast)
        {
            audMan.FlushQueue(true);
            audMan.QueueAudio(isFast ? NoisePlugin.noiseFastForward : NoisePlugin.noiseIdle);
            audMan.SetLoop(true);
        }

        if (isNoiseMoving)
        {
            float dist = Vector3.Distance(noiseBody.transform.position, lastFrameNoisePos);
            distanceMoved += dist;

            if (distanceMoved >= 5f)
            {
                audMan.PlaySingle(NoisePlugin.noiseStep);
                distanceMoved %= 5f;
            }
        }

        lastFrameNoisePos = noiseBody.transform.position;
    }

    private void UpdateAnimation(bool isMoving, float speed)
    {
        if (noiseAnimator == null) return;

        noiseAnimator.speed = isMoving ? 1f : 0f;

        SetAnimatorBoolIfExists("Walking", isMoving);
        SetAnimatorBoolIfExists("isWalking", isMoving);
        SetAnimatorBoolIfExists("Walk", isMoving);
        SetAnimatorFloatIfExists("Speed", isMoving ? speed : 0f);
    }

    private void SetAnimatorBoolIfExists(string paramName, bool value)
    {
        foreach (var param in noiseAnimator.parameters)
        {
            if (param.type == AnimatorControllerParameterType.Bool && param.name == paramName)
            {
                noiseAnimator.SetBool(paramName, value);
                break;
            }
        }
    }

    private void SetAnimatorFloatIfExists(string paramName, float value)
    {
        foreach (var param in noiseAnimator.parameters)
        {
            if (param.type == AnimatorControllerParameterType.Float && param.name == paramName)
            {
                noiseAnimator.SetFloat(paramName, value);
                break;
            }
        }
    }

    private void OnDestroy()
    {
        if (vignetteOverlay != null) Destroy(vignetteOverlay);
        if (currentTexts != null) Destroy(currentTexts);
    }
}

[HarmonyPatch(typeof(LocalizationManager), "LoadLocalizedText")]
internal static class LocalizationManager_LoadLocalizedText_Patch
{
    static void Postfix(LocalizationManager __instance)
    {
        if (AccessTools.Field(typeof(LocalizationManager), "localizedText").GetValue(__instance) is Dictionary<string, string> dict)
        {
            dict["Noise_Sfx_Fastforward"] = "[Fastforward]";
            dict["Noise_Sfx_Footsteps"] = "[Step]";
            dict["Noise_Sfx_Idle"] = "[Static]";
            dict["Noise_Sfx_Pause"] = "[Pause]";

            dict["Noise_Vfx_Play"] = "PLAY <sprite index=0>";
            dict["Noise_Vfx_Pause"] = "PAUSE <sprite index=0>";
            dict["Noise_Vfx_Fastforward"] = "FF <sprite index=0>";
        }
    }
}
