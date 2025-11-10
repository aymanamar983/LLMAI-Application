using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using System.Text;

[System.Serializable]
public class APIRequest
{
    public string message;
    public string model = "default";
}

[System.Serializable]
public class APIResponse
{
    public string response;
    public string error;
}

public class APITester : MonoBehaviour
{
    private string baseUrl = "http://localhost:3001/api/v1";
    private string workspaceSlug = "Ayesha";
    private string apiKey = "HXQ6RNY-KHU2R8-K1VCINQ-65G1DK9";

    void Start()
    {
        // Test the connection when the game starts
        StartCoroutine(TestConnection());
    }

    public void SendMessageToAI(string userMessage)
    {
        StartCoroutine(SendChatRequest(userMessage));
    }

    private IEnumerator TestConnection()
    {
        Debug.Log("Testing API connection...");
        string testUrl = $"{baseUrl}/workspace/{workspaceSlug}/chat";
        Debug.Log("API URL: " + testUrl);

        // Send a simple test message
        yield return StartCoroutine(SendChatRequest("Hello, are you working?"));
    }

    private IEnumerator SendChatRequest(string message)
    {
        // Create the correct URL structure
        string url = $"{baseUrl}/workspace/{workspaceSlug}/chat";

        // Create request data
        APIRequest requestData = new APIRequest
        {
            message = message,
            model = "default"
        };

        string jsonData = JsonUtility.ToJson(requestData);
        byte[] rawData = Encoding.UTF8.GetBytes(jsonData);

        Debug.Log("Sending request to: " + url);
        Debug.Log("Message: " + message);

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(rawData);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            request.SetRequestHeader("X-API-Key", apiKey);

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("✅ API Request Successful!");
                Debug.Log("Raw Response: " + request.downloadHandler.text);

                // Parse the response
                HandleAPIResponse(request.downloadHandler.text);
            }
            else
            {
                Debug.LogError($"❌ API Request Failed: {request.responseCode}");
                Debug.LogError("Error: " + request.error);
                Debug.LogError("Response: " + request.downloadHandler.text);

                // Try to get more info about the error
                if (request.downloadHandler.text.Contains("workspace"))
                {
                    Debug.LogError("Workspace might be invalid. Checking available workspaces...");
                    yield return StartCoroutine(CheckWorkspaces());
                }
            }
        }
    }

    private IEnumerator CheckWorkspaces()
    {
        string url = $"{baseUrl}/workspaces";

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("Available workspaces: " + request.downloadHandler.text);
            }
            else
            {
                Debug.LogError("Could not retrieve workspaces: " + request.error);
            }
        }
    }

    private void HandleAPIResponse(string jsonResponse)
    {
        try
        {
            // Try to parse as JSON first
            APIResponse response = JsonUtility.FromJson<APIResponse>(jsonResponse);

            if (!string.IsNullOrEmpty(response.error))
            {
                Debug.LogError("API returned error: " + response.error);
            }
            else if (!string.IsNullOrEmpty(response.response))
            {
                Debug.Log("🤖 AI Response: " + response.response);
                // Here you can display the response in your UI or use it in-game
            }
            else
            {
                // If parsing fails, show raw response
                Debug.Log("Raw API Response: " + jsonResponse);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Failed to parse JSON, showing raw response: " + jsonResponse);
            Debug.Log("AI Response: " + jsonResponse);
        }
    }
}