using LeastSquares.Overtone; // For TTS
using System.Collections;
using System.IO;
using TMPro;
using UnityEngine;
using LeastSquares.Undertone;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Windows.Speech; // For speech recognition
using UnityEngine.EventSystems; // For pointer events
using System.Text;
using System.Text.RegularExpressions;

public class ChatAIManager : MonoBehaviour
{
    [Header("UI References")]
    public InputField userInput;
    public Text chatLog;
    public Text statusText;
    public Button sendButton;
    public Button voiceInputButton;

    [Header("Scrolling")]
    [Tooltip("Assign the ScrollRect that contains the chatLog as its Content")]
    public ScrollRect chatScrollRect;
    [Tooltip("Auto-scroll to bottom when new messages arrive")]
    public bool autoScrollToBottom = true;

    [Header("API Settings (Loaded from StreamingAssets)")]
    [Tooltip("These are loaded from Assets/StreamingAssets/config.json at runtime.")]
    public string baseUrl = "";          
    public string workspaceSlug = "";    
    public string apiKey = "";           

    [Header("Settings")]
    public int maxChatLines = 50;
    public string jsonFileName = "ai_response.json";
    public bool enableDebugLogs = true;

    [Header("Voice Chat Settings")]
    public TTSPlayer ttsPlayer;
    public PushToTranscribe speechRecognizer;

    [Header("Config Loading")]
    [Tooltip("StreamingAssets relative path to the config JSON.")]
    public string configFileName = "config.json";
    [Tooltip("If true, Start() will halt flow until config is loaded.")]
    public bool blockUntilConfigLoaded = true;

    [Header("TTS Cleanup")]
    [Tooltip("If true, symbols like *, #, @, etc. are stripped before TTS so they aren't spoken.")]
    public bool stripSymbolsForTTS = true;

    [Header("Chat Behavior")]
    [Tooltip("If true, each new chat will clear previous conversation")]
    public bool clearChatOnNewSession = true;
    [Tooltip("If true, messages will appear one by one with typing effect")]
    public bool useTypingEffect = true;
    [Tooltip("Typing speed in characters per second")]
    public float typingSpeed = 30f;

    private bool isProcessing = false;
    private string sessionId; // Will be regenerated for each new session
    private bool isVoiceInputActive = false;
    private Coroutine scrollCoroutine;
    private Coroutine typingCoroutine;
    private StringBuilder currentChatSession = new StringBuilder();
    private bool isNewChatSession = true;

    private string ApiUrl => $"{baseUrl}/v1/workspace/{workspaceSlug}/chat";

    // Strongly-typed config container
    [System.Serializable]
    public class ChatConfig
    {
        public string baseUrl;
        public string workspaceSlug;
        public string apiKey;
    }

    // ─────────────────────────────────────────────────────────────────────────────

    private IEnumerator Start()
    {
        // Generate initial session ID
        GenerateNewSessionId();

        // Load config from StreamingAssets before anything else that uses it.
        if (blockUntilConfigLoaded)
        {
            yield return LoadConfigFromStreamingAssets();
        }
        else
        {
            StartCoroutine(LoadConfigFromStreamingAssets());
        }

        // After config load, continue initializing UI and systems.
        if (userInput != null)
            userInput.onSubmit.AddListener(delegate { OnSendClicked(); });

        if (sendButton != null) sendButton.interactable = true;

        // Setup voice input button for hold-to-talk functionality
        if (voiceInputButton != null)
        {
            voiceInputButton.onClick.RemoveAllListeners();

            EventTrigger trigger = voiceInputButton.gameObject.GetComponent<EventTrigger>();
            if (trigger == null)
            {
                trigger = voiceInputButton.gameObject.AddComponent<EventTrigger>();
            }
            else
            {
                trigger.triggers.Clear();
            }

            EventTrigger.Entry pointerDownEntry = new EventTrigger.Entry();
            pointerDownEntry.eventID = EventTriggerType.PointerDown;
            pointerDownEntry.callback.AddListener((data) => { OnVoiceButtonPointerDown(); });
            trigger.triggers.Add(pointerDownEntry);

            EventTrigger.Entry pointerUpEntry = new EventTrigger.Entry();
            pointerUpEntry.eventID = EventTriggerType.PointerUp;
            pointerUpEntry.callback.AddListener((data) => { OnVoiceButtonPointerUp(); });
            trigger.triggers.Add(pointerUpEntry);
        }

        if (speechRecognizer != null)
        {
            speechRecognizer.OnTranscriptionComplete.AddListener(ProcessVoiceInput);
        }

        InitializeTTS();

        // Ensure ScrollRect & Content are correctly configured
        EnsureScrollSetup();

        // Initialize with empty chat or welcome message
        if (clearChatOnNewSession)
        {
            ClearChatLog();
        }

        if (enableDebugLogs) Debug.Log($"API URL: {ApiUrl}");
    }

    // Generate a new session ID for each new chat session
    private void GenerateNewSessionId()
    {
        sessionId = $"unity-chat-session-{System.Guid.NewGuid().ToString().Substring(0, 8)}";
        if (enableDebugLogs) Debug.Log($"New session ID: {sessionId}");
    }

    // Load config.json from StreamingAssets (cross-platform safe)
    private IEnumerator LoadConfigFromStreamingAssets()
    {
        string path = Path.Combine(Application.streamingAssetsPath, configFileName);

        if (enableDebugLogs) Debug.Log($"Loading chat config from: {path}");

        string json = null;

#if UNITY_ANDROID && !UNITY_EDITOR
        // On Android, StreamingAssets are in a compressed jar, require UnityWebRequest.
        using (UnityWebRequest req = UnityWebRequest.Get(path))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Failed to load config from StreamingAssets: {req.error}");
            }
            else
            {
                json = req.downloadHandler.text;
            }
        }
#else
        // On other platforms we can read directly.
        try
        {
            if (File.Exists(path))
            {
                json = File.ReadAllText(path);
            }
            else
            {
                Debug.LogError($"Config file not found at: {path}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error reading config file: {e.Message}");
        }
        yield return null;
#endif

        if (string.IsNullOrEmpty(json))
        {
            Debug.LogWarning("Config JSON empty or missing. Using existing inspector values if any.");
            yield break;
        }

        ChatConfig cfg = null;
        try
        {
            cfg = JsonUtility.FromJson<ChatConfig>(json);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to parse config JSON: {e.Message}\nJSON: {json}");
        }

        if (cfg == null)
        {
            Debug.LogError("Config parse returned null. Check your JSON format.");
            yield break;
        }

        // Apply loaded values (only overwrite if present)
        if (!string.IsNullOrWhiteSpace(cfg.baseUrl)) baseUrl = cfg.baseUrl.TrimEnd('/'); // normalize
        if (!string.IsNullOrWhiteSpace(cfg.workspaceSlug)) workspaceSlug = cfg.workspaceSlug.Trim();
        if (!string.IsNullOrWhiteSpace(cfg.apiKey)) apiKey = cfg.apiKey.Trim();

        if (enableDebugLogs)
        {
            Debug.Log($"Config loaded. baseUrl={baseUrl}, workspaceSlug={workspaceSlug}, apiKey={(string.IsNullOrEmpty(apiKey) ? "(empty)" : "(loaded)")}");
        }

        // Quick sanity check
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(workspaceSlug) || string.IsNullOrEmpty(apiKey))
        {
            Debug.LogWarning("One or more config fields are empty. Requests may fail until you fill config.json.");
        }
    }

    private void InitializeTTS()
    {
        if (ttsPlayer != null && ttsPlayer.source == null)
        {
            ttsPlayer.source = ttsPlayer.gameObject.AddComponent<AudioSource>();

            if (ttsPlayer.Engine == null)
            {
                Debug.LogWarning("TTS Engine not assigned. Text-to-speech may not work properly.");
            }
        }
    }

    public void OnSendClicked()
    {
        if (isProcessing) return;
        string message = userInput.text.Trim();
        if (string.IsNullOrEmpty(message)) return;

        // Clear previous chat if this is a new session
        if (isNewChatSession && clearChatOnNewSession)
        {
            ClearChatLog();
            isNewChatSession = false;
        }

        AppendMessage("You", message, false); // User message appears instantly
        userInput.text = "";
        StartCoroutine(SendMessageToLLM(message));
    }

    // NEW METHOD: Start a new chat session manually
    public void StartNewChatSession()
    {
        if (isProcessing) return;

        ClearChatLog();
        GenerateNewSessionId();
        isNewChatSession = false; // Set to false because we're starting fresh

        statusText.text = "New chat session started";
        StartCoroutine(ClearStatusAfterDelay(2f));

        if (enableDebugLogs) Debug.Log("Started new chat session");
    }

    private void OnVoiceButtonPointerDown()
    {
        if (isProcessing || isVoiceInputActive) return;

        // ALWAYS start a new chat session when voice button is pressed
        StartNewChatSession();

        // Cut any ongoing TTS immediately so user isn't waiting to speak
        StopSpeakingIfAny();

        speechRecognizer?.StartRecording();
        OnRecordingStarted();
    }

    private void OnVoiceButtonPointerUp()
    {
        if (!isVoiceInputActive) return;

        speechRecognizer?.StopRecordingAndProcess();

        // show "Transcribing..." immediately to signal progress
        OnRecordingStopped(transcribing: true);
    }

    private void OnRecordingStarted()
    {
        isVoiceInputActive = true;
        if (voiceInputButton != null)
        {
            var colors = voiceInputButton.colors;
            colors.normalColor = Color.red;
            colors.highlightedColor = new Color(1f, 0.5f, 0.5f);
            voiceInputButton.colors = colors;
        }
        statusText.text = "🎤 Listening...";
    }

    // Add parameter to choose the next status after recording stops
    private void OnRecordingStopped(bool transcribing = false)
    {
        isVoiceInputActive = false;
        if (voiceInputButton != null)
        {
            var colors = voiceInputButton.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.95f, 0.95f, 0.95f);
            voiceInputButton.colors = colors;
        }
        statusText.text = transcribing ? "📝 Transcribing..." : "⏳ Processing...";
    }

    private void ProcessVoiceInput(string transcription)
    {
        // Don't call OnRecordingStopped() again here (avoids extra flip)
        // We already set "📝 Transcribing..." in PointerUp.

        if (!string.IsNullOrEmpty(transcription))
        {
            // switch to Thinking FIRST, then send — feels snappier
            statusText.text = "Thinking...";
            userInput.text = transcription;
            AppendMessage("You", transcription, false); // User message appears instantly
            OnSendClicked();
        }
        else
        {
            statusText.text = "Couldn't understand voice input. Please try again.";
            StartCoroutine(ClearStatusAfterDelay(3f));
        }
    }

    private IEnumerator ClearStatusAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        statusText.text = "";
    }

    private IEnumerator SendMessageToLLM(string userMessage)
    {
        // Guard if config not loaded
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(workspaceSlug) || string.IsNullOrEmpty(apiKey))
        {
            AppendMessage("System", "Config not loaded or incomplete. Please check Assets/StreamingAssets/config.json.", false);
            yield break;
        }

        isProcessing = true;
        statusText.text = "Thinking...";
        if (sendButton != null) sendButton.interactable = false;

        ChatRequest req = new ChatRequest(userMessage, "chat", sessionId, null, false);
        string payload = JsonUtility.ToJson(req);
        if (enableDebugLogs) Debug.Log($"Sending payload: {payload}");

        using (UnityWebRequest request = new UnityWebRequest(ApiUrl, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(payload);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("accept", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);
            request.timeout = 30;

            yield return request.SendWebRequest();

            isProcessing = false;
            if (sendButton != null) sendButton.interactable = true;

            if (request.result == UnityWebRequest.Result.Success)
            {
                statusText.text = "";
                string jsonResponse = request.downloadHandler.text;
                SaveJsonToFile(jsonResponse);
                if (enableDebugLogs) Debug.Log($"Raw response: {jsonResponse}");

                try
                {
                    AIResponse resp = JsonUtility.FromJson<AIResponse>(jsonResponse);
                    string aiText = string.IsNullOrEmpty(resp.textResponse) ?
                        "I didn't get a response. Could you try again?" : resp.textResponse;

                    // AI message appears with typing effect
                    AppendMessage("AI", aiText, useTypingEffect);

                    if (ttsPlayer != null) StartCoroutine(SpeakText(aiText));
                }
                catch (System.Exception e)
                {
                    AppendMessage("AI", "Sorry, I had trouble understanding the response.", false);
                    Debug.LogError($"JSON Parse Error: {e.Message}");
                    Debug.LogError($"Raw JSON: {jsonResponse}");
                }
            }
            else
            {
                string errorMessage = GetUserFriendlyErrorMessage(request);
                AppendMessage("AI", errorMessage, false);
                statusText.text = "Error occurred";

                Debug.LogError($"API Request Failed: {request.error} | {request.downloadHandler.text}");
                Debug.LogError($"Status Code: {request.responseCode}");
            }
        }
    }

    private string GetUserFriendlyErrorMessage(UnityWebRequest request)
    {
        if (request.responseCode == 401)
            return "Authentication failed. Please check your API key.";
        else if (request.responseCode == 404)
            return "API endpoint not found. Please check your configuration.";
        else if (request.responseCode >= 500)
            return "Server error. Please try again later.";
        else
            return "Sorry, I'm having trouble connecting right now. Please try again.";
    }

    // remove unwanted symbols before speaking so TTS won't say them
    private string RemoveSymbols(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        // Allow letters, digits, whitespace, and these punctuation marks.
        const string allowedPunctuation = ",.!?;:'\"-()[]{}";
        StringBuilder sb = new StringBuilder(input.Length);

        foreach (char c in input)
        {
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || allowedPunctuation.IndexOf(c) >= 0)
            {
                sb.Append(c);
            }
            // else skip symbol (e.g., *, #, @, $, %, ^, &, ~, `, |, \, /, <, >, +, =, _)
        }

        // Optional: collapse excessive whitespace created by removals
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private IEnumerator SpeakText(string text)
    {
        if (ttsPlayer == null || ttsPlayer.Engine == null)
        {
            Debug.LogWarning("TTS not properly configured. Skipping speech.");
            yield break;
        }

        if (ttsPlayer.source == null)
        {
            ttsPlayer.source = ttsPlayer.gameObject.AddComponent<AudioSource>();
        }

        // Strip symbols before speaking (controlled by toggle)
        string toSpeak = stripSymbolsForTTS ? RemoveSymbols(text) : text;

        AudioClip clip = null;
        var speakTask = ttsPlayer.Engine.Speak(toSpeak, ttsPlayer.Voice?.VoiceModel);
        while (!speakTask.IsCompleted) yield return null;
        clip = speakTask.Result;

        if (clip != null)
        {
            ttsPlayer.source.clip = clip;
            ttsPlayer.source.Play();

            statusText.text = "🔊 Speaking...";
            yield return new WaitForSeconds(clip.length);
            statusText.text = "";
        }
    }

    // stop any current speech instantly (used when user starts talking)
    private void StopSpeakingIfAny()
    {
        if (ttsPlayer != null && ttsPlayer.source != null && ttsPlayer.source.isPlaying)
        {
            ttsPlayer.source.Stop();
            statusText.text = ""; // clear "🔊 Speaking..."
        }
    }

    private void AppendMessage(string sender, string message, bool useTypingEffect)
    {
        if (typingCoroutine != null)
        {
            StopCoroutine(typingCoroutine);
            typingCoroutine = null;
        }

        string formattedMessage = $"\n<b>{sender}:</b> ";

        if (useTypingEffect)
        {
            // Start typing effect coroutine
            typingCoroutine = StartCoroutine(TypeTextEffect(sender, message, formattedMessage));
        }
        else
        {
            // Show message instantly
            formattedMessage += message;
            UpdateChatLog(formattedMessage);
        }
    }

    private IEnumerator TypeTextEffect(string sender, string message, string prefix)
    {
        statusText.text = "AI is typing...";

        // Show the prefix (sender name) immediately
        string currentText = chatLog.text + prefix;
        chatLog.text = currentText;

        // Type out the message character by character
        StringBuilder typedMessage = new StringBuilder();
        for (int i = 0; i < message.Length; i++)
        {
            typedMessage.Append(message[i]);
            chatLog.text = currentText + typedMessage.ToString();

            // Update layout and scroll
            UpdateChatLayout();
            ScrollToBottom();

            float delay = 1f / typingSpeed;
            yield return new WaitForSeconds(delay);
        }

        statusText.text = "";
        typingCoroutine = null;
    }

    private void UpdateChatLog(string newMessage)
    {
        // For new sessions or when clearing chat, replace the entire content
        if (isNewChatSession && clearChatOnNewSession)
        {
            chatLog.text = newMessage.TrimStart('\n');
            isNewChatSession = false;
        }
        else
        {
            // For continuing conversations, append to existing content
            chatLog.text += newMessage;
        }

        UpdateChatLayout();
        ScrollToBottom();
    }

    private void ClearChatLog()
    {
        chatLog.text = "";
        currentChatSession.Clear();
        isNewChatSession = true;
        UpdateChatLayout();
    }

    private void UpdateChatLayout()
    {
        // Rebuild layout so the ScrollRect knows the new content height
        Canvas.ForceUpdateCanvases();
        if (chatScrollRect != null && chatScrollRect.content != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(chatScrollRect.content);
        }
        else
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(chatLog.rectTransform);
        }
    }

    private void ScrollToBottom()
    {
        if (autoScrollToBottom && chatScrollRect != null)
        {
            if (scrollCoroutine != null) StopCoroutine(scrollCoroutine);
            scrollCoroutine = StartCoroutine(SnapToBottomAfterLayout());
        }
    }

    // ---- SCROLL AUTO-HEAL + RELIABLE SNAP ----
    private void EnsureScrollSetup()
    {
        if (chatScrollRect == null && chatLog != null)
        {
            chatScrollRect = chatLog.GetComponentInParent<ScrollRect>();
            if (enableDebugLogs && chatScrollRect == null)
                Debug.LogWarning("Chat ScrollRect not assigned and not found in parents.");
        }
        if (chatScrollRect == null) return;

        // Make sure Content is assigned
        if (chatScrollRect.content == null && chatLog != null)
            chatScrollRect.content = chatLog.rectTransform;

        if (chatScrollRect.content == null) return;

        // Content anchors/pivot as Top-Stretch with top pivot
        var rt = chatScrollRect.content;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = Vector2.zero;

        // Ensure content auto-sizes to its preferred height
        var fitter = rt.GetComponent<ContentSizeFitter>();
        if (fitter == null) fitter = rt.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Clamped movement avoids rubber-banding
        chatScrollRect.movementType = ScrollRect.MovementType.Clamped;
    }

    // call this any time you change text and want to stick to bottom
    private IEnumerator SnapToBottomAfterLayout()
    {
        // allow layout to rebuild (sometimes needs 2 frames in complex canvases)
        yield return null;
        yield return new WaitForEndOfFrame();

        Canvas.ForceUpdateCanvases();
        if (chatScrollRect != null && chatScrollRect.content != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(chatScrollRect.content);
        }

        // snap twice to beat one more internal layout pass
        chatScrollRect.verticalNormalizedPosition = 0f;
        Canvas.ForceUpdateCanvases();
        chatScrollRect.verticalNormalizedPosition = 0f;
    }

    private void SaveJsonToFile(string json, string fileName = null)
    {
        try
        {
            string path = Path.Combine(Application.persistentDataPath, fileName ?? jsonFileName);
            File.WriteAllText(path, json);
            if (enableDebugLogs) Debug.Log("Saved JSON to: " + path);
        }
        catch (System.Exception e)
        {
            Debug.LogError("Failed to save JSON file: " + e.Message);
        }
    }

    void Update()
    {
        if (sendButton != null)
            sendButton.interactable = !isProcessing && !string.IsNullOrWhiteSpace(userInput.text);

        if (Input.GetKeyDown(KeyCode.C) && Input.GetKey(KeyCode.LeftControl) && !isProcessing)
        {
            StartCoroutine(TestExactCurlCoroutine());
        }

        if (Input.GetKeyDown(KeyCode.V) && !isProcessing && !isVoiceInputActive)
        {
            OnVoiceButtonPointerDown();
        }

        if (Input.GetKeyUp(KeyCode.V) && isVoiceInputActive)
        {
            OnVoiceButtonPointerUp();
        }

        // Clear chat and start new session with Ctrl+N (New Chat)
        if (Input.GetKeyDown(KeyCode.N) && Input.GetKey(KeyCode.LeftControl))
        {
            StartNewChatSession();
        }

        // Optional manual scroll shortcuts
        if (Input.GetKeyDown(KeyCode.Home) && chatScrollRect != null)
        {
            chatScrollRect.verticalNormalizedPosition = 1f;
        }

        if (Input.GetKeyDown(KeyCode.End) && chatScrollRect != null)
        {
            chatScrollRect.verticalNormalizedPosition = 0f;
        }

        if (Input.GetKeyDown(KeyCode.A))
        {
            autoScrollToBottom = !autoScrollToBottom;
            statusText.text = autoScrollToBottom ? "Auto-scroll: ON" : "Auto-scroll: OFF";
            StartCoroutine(ClearStatusAfterDelay(2f));
        }

        // Toggle typing effect with T key
        if (Input.GetKeyDown(KeyCode.T))
        {
            useTypingEffect = !useTypingEffect;
            statusText.text = useTypingEffect ? "Typing effect: ON" : "Typing effect: OFF";
            StartCoroutine(ClearStatusAfterDelay(2f));
        }
    }

    private IEnumerator TestExactCurlCoroutine()
    {
        statusText.text = "Testing API connection...";
        string testUrl = $"{baseUrl}/v1/workspace/{workspaceSlug}/chat";

        ChatRequest testReq = new ChatRequest("Hello", "query", "test-session-id", null, false);
        string payload = JsonUtility.ToJson(testReq);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(payload);

        using (UnityWebRequest testRequest = new UnityWebRequest(testUrl, "POST"))
        {
            testRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
            testRequest.downloadHandler = new DownloadHandlerBuffer();
            testRequest.SetRequestHeader("Content-Type", "application/json");
            testRequest.SetRequestHeader("accept", "application/json");
            testRequest.SetRequestHeader("Authorization", "Bearer " + apiKey);

            yield return testRequest.SendWebRequest();

            if (testRequest.result == UnityWebRequest.Result.Success)
            {
                AppendMessage("System", "✓ API connection test successful", false);
                statusText.text = "Connection test passed";
            }
            else
            {
                AppendMessage("System", $"✗ API connection failed: {testRequest.error}", false);
                statusText.text = "Connection test failed";
            }

            yield return new WaitForSeconds(2f);
            statusText.text = "";
        }
    }

    [System.Serializable]
    public class ChatRequest
    {
        public string message;
        public string mode;
        public string sessionId;
        public object[] attachments;
        public bool reset;

        public ChatRequest(string msg, string mod, string sessId, object[] attach, bool rst)
        {
            message = msg;
            mode = mod;
            sessionId = sessId;
            attachments = attach;
            reset = rst;
        }
    }

    [System.Serializable]
    public class AIResponse
    {
        public string textResponse;
    }

    private void OnDestroy()
    {
        if (speechRecognizer != null && speechRecognizer.OnTranscriptionComplete != null)
        {
            speechRecognizer.OnTranscriptionComplete.RemoveListener(ProcessVoiceInput);
        }
    }
}