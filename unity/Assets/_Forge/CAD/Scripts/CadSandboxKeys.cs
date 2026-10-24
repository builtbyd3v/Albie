using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// CADSandbox shortcuts: 1-4 jump to a mock design, Left/Right step, E explode, U undo, R reset, F fly.
/// </summary>
public class CadSandboxKeys : MonoBehaviour
{
    void Update()
    {
        var k = Keyboard.current;
        var s = CadSession.Instance;
        if (k == null || s == null || !s.Ready) return;
        if (k.digit1Key.wasPressedThisFrame) s.ShowMockVersion(0);
        if (k.digit2Key.wasPressedThisFrame) s.ShowMockVersion(1);
        if (k.digit3Key.wasPressedThisFrame) s.ShowMockVersion(2);
        if (k.digit4Key.wasPressedThisFrame) s.ShowMockVersion(3);
        if (k.rightArrowKey.wasPressedThisFrame) s.StepMock(1);
        if (k.leftArrowKey.wasPressedThisFrame) s.StepMock(-1);
        if (k.eKey.wasPressedThisFrame) s.SetView(s.View.explode != true, s.View.focusId);
        if (k.uKey.wasPressedThisFrame) s.Undo();
        if (k.rKey.wasPressedThisFrame) s.ResetDesign();
        if (k.fKey.wasPressedThisFrame) s.RequestFlight();
    }
}
