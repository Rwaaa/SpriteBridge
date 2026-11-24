#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SpriteBridge.Editor
{
    /// <summary>
    /// 1-Click Custom Shader Patcher & Shader Graph helper window for SpriteBridge.
    /// Generates a SpriteBridge-compatible GPU indirect shader wrapper or inserts the required
    /// SpriteBridgeInclude.hlsl directives into a custom URP Sprite HLSL shader.
    /// </summary>
    public sealed class SpriteBridgeShaderPatcherWindow : EditorWindow
    {
        private Shader _sourceShader;
        private string _statusMessage;
        private MessageType _statusType = MessageType.Info;

        [MenuItem("Tools/SpriteBridge/Custom Shader Auto-Patcher...", false, 80)]
        public static void OpenWindow()
        {
            SpriteBridgeShaderPatcherWindow win = GetWindow<SpriteBridgeShaderPatcherWindow>("SpriteBridge Shader Patcher");
            win.minSize = new Vector2(460f, 320f);
            win.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("SpriteBridge 1-Click Custom Shader Auto-Patcher", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Select any custom URP Sprite .shader asset to generate a '_SpriteBridge.shader' copy with " +
                "SpriteBridgeInclude.hlsl and SV_InstanceID GPU indirect reconstruction.\n\n" +
                "For Shader Graph: add a Custom Function node pointing to 'SpriteBridgeShaderGraphNode.hlsl' " +
                "(Function Name: SpriteBridgeVertex).",
                MessageType.Info);

            EditorGUILayout.Space(8);
            _sourceShader = (Shader)EditorGUILayout.ObjectField("Source Shader", _sourceShader, typeof(Shader), false);

            EditorGUILayout.Space(8);
            GUI.enabled = _sourceShader != null;
            if (GUILayout.Button("Generate Patched _SpriteBridge.shader Copy", GUILayout.Height(30f)))
            {
                PatchSelectedShader(_sourceShader);
            }
            GUI.enabled = true;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Manual HLSL Snippet (Copy & Paste):", EditorStyles.boldLabel);
            const string snippet =
                "#pragma target 4.5\n" +
                "#pragma multi_compile _ _SPRITEBRIDGE_SORTED\n" +
                "#include \"Assets/SpriteBridge/Shaders/SpriteBridgeInclude.hlsl\"\n" +
                "// In Attributes: uint instanceID : SV_InstanceID;\n" +
                "// In Vertex Shader:\n" +
                "float3 posWS; float2 spriteUV; half4 spriteCol; float3 normWS; float3 tanWS;\n" +
                "SpriteBridgeReconstructVertex(input.instanceID, input.uv, posWS, spriteUV, spriteCol, normWS, tanWS);";
            EditorGUILayout.TextArea(snippet, GUILayout.Height(110f));

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
        }

        private void PatchSelectedShader(Shader shader)
        {
            string assetPath = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".shader", System.StringComparison.OrdinalIgnoreCase))
            {
                _statusMessage = $"Shader '{shader.name}' is a built-in or non-.shader asset ('{assetPath}'). Use one of the built-in SpriteBridge shaders (Unlit / Lit2D / Lit3D) or select a project .shader file.";
                _statusType = MessageType.Warning;
                return;
            }

            string fullPath = Path.GetFullPath(assetPath);
            if (!File.Exists(fullPath))
            {
                _statusMessage = $"File not found on disk: {fullPath}";
                _statusType = MessageType.Error;
                return;
            }

            string src = File.ReadAllText(fullPath, Encoding.UTF8);
            if (src.Contains("SpriteBridgeInclude.hlsl"))
            {
                _statusMessage = $"'{assetPath}' already includes SpriteBridgeInclude.hlsl.";
                _statusType = MessageType.Info;
                return;
            }

            string includePath = Directory.Exists(Path.GetFullPath("Assets/SpriteBridge/Shaders"))
                ? "Assets/SpriteBridge/Shaders/SpriteBridgeInclude.hlsl"
                : "Packages/com.spritebridge.core/Shaders/SpriteBridgeInclude.hlsl";

            // Patch shader name header and inject include after HLSLPROGRAM
            string patched = src;
            int firstQuote = patched.IndexOf('"');
            int secondQuote = firstQuote >= 0 ? patched.IndexOf('"', firstQuote + 1) : -1;
            if (firstQuote >= 0 && secondQuote > firstQuote)
            {
                string origShaderName = patched.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                patched = patched.Substring(0, firstQuote + 1) +
                          "SpriteBridge/Patched/" + origShaderName +
                          patched.Substring(secondQuote);
            }

            patched = patched.Replace(
                "HLSLPROGRAM",
                "HLSLPROGRAM\n            #pragma target 4.5\n            #pragma multi_compile _ _SPRITEBRIDGE_SORTED\n            #include \"" + includePath + "\"");

            string outAssetPath = assetPath.Substring(0, assetPath.Length - ".shader".Length) + "_SpriteBridge.shader";
            File.WriteAllText(Path.GetFullPath(outAssetPath), patched, Encoding.UTF8);
            AssetDatabase.ImportAsset(outAssetPath);

            Shader newShader = AssetDatabase.LoadAssetAtPath<Shader>(outAssetPath);
            if (newShader != null)
            {
                Selection.activeObject = newShader;
            }

            _statusMessage = $"Created patched shader at '{outAssetPath}'. Connect SpriteBridgeReconstructVertex(input.instanceID, ...) in its vertex function if it uses custom vertex math.";
            _statusType = MessageType.Info;
        }
    }
}
#endif
