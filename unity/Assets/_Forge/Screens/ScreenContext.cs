using System.Collections;
using System.IO;
using UnityEngine;

/// <summary>
/// Puts what VS Code has open on the panel header: "rocket.py · 2 errors". Polls the hub's GET /vscode/context
/// (the Forge Bit extension's /context, passed through). VS Code closed or hub off with no mock: no subtitle.
/// </summary>
public class ScreenContext : MonoBehaviour
{
    [Tooltip("Seconds between polls.")]
    public float interval = 2f;

    class VscodeContext
    {
        public string file;
        public int errors;
    }

    ForgePanel panel;

    void Awake() => panel = GetComponent<ForgePanel>();

    void OnEnable()
    {
        if (panel != null) StartCoroutine(Poll());
    }

    IEnumerator Poll()
    {
        var wait = new WaitForSeconds(interval);
        while (true)
        {
            ForgeNet.Get<VscodeContext>("/vscode/context", c => Show(Format(c)), _ => Show(""));
            yield return wait;
        }
    }

    static string Format(VscodeContext c)
    {
        if (c == null || string.IsNullOrEmpty(c.file)) return "";
        string name = Path.GetFileName(c.file);
        return c.errors == 0 ? name : $"{name} · {c.errors} error{(c.errors == 1 ? "" : "s")}";
    }

    // Only on change: the setter re-lays out the header.
    void Show(string text)
    {
        if (panel.Subtitle != text) panel.Subtitle = text;
    }
}
