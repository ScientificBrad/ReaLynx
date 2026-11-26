using System.Runtime.InteropServices;
using ReaLynx.Core.Suzy;

namespace ReaLynx.Win;

/// <summary>
/// Zero-dependency XInput controller reader (xinput1_4.dll with xinput9_1_0.dll fallback).
/// </summary>
public static class XInputController
{
    private const ushort DPAD_UP     = 0x0001;
    private const ushort DPAD_DOWN   = 0x0002;
    private const ushort DPAD_LEFT   = 0x0004;
    private const ushort DPAD_RIGHT  = 0x0008;
    private const ushort START       = 0x0010;
    private const ushort BACK        = 0x0020;
    private const ushort LB          = 0x0100;
    private const ushort RB          = 0x0200;
    private const ushort BTN_A       = 0x1000;
    private const ushort BTN_B       = 0x2000;
    private const ushort BTN_X       = 0x4000;
    private const ushort BTN_Y       = 0x8000;
    private const short Deadzone     = 8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState14(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState910(uint dwUserIndex, out XINPUT_STATE pState);

    private static bool _useFallback;
    private static bool _disabled;

    public static LynxButtons Poll(uint controllerIndex = 0)
    {
        if (_disabled) return LynxButtons.None;

        XINPUT_STATE state;
        uint res;
        try
        {
            res = _useFallback
                ? XInputGetState910(controllerIndex, out state)
                : XInputGetState14(controllerIndex, out state);
        }
        catch (DllNotFoundException)
        {
            if (!_useFallback)
            {
                _useFallback = true;
                return Poll(controllerIndex);
            }
            _disabled = true;
            return LynxButtons.None;
        }

        if (res != 0) return LynxButtons.None;

        var g = state.Gamepad;
        LynxButtons b = LynxButtons.None;

        if ((g.wButtons & DPAD_UP) != 0 || g.sThumbLY > Deadzone) b |= LynxButtons.Up;
        if ((g.wButtons & DPAD_DOWN) != 0 || g.sThumbLY < -Deadzone) b |= LynxButtons.Down;
        if ((g.wButtons & DPAD_LEFT) != 0 || g.sThumbLX < -Deadzone) b |= LynxButtons.Left;
        if ((g.wButtons & DPAD_RIGHT) != 0 || g.sThumbLX > Deadzone) b |= LynxButtons.Right;

        if ((g.wButtons & BTN_A) != 0) b |= LynxButtons.A;
        if ((g.wButtons & (BTN_B | BTN_X)) != 0) b |= LynxButtons.B;
        if ((g.wButtons & (LB | BTN_Y)) != 0) b |= LynxButtons.Option1;
        if ((g.wButtons & (RB | BACK)) != 0) b |= LynxButtons.Option2;
        if ((g.wButtons & START) != 0) b |= LynxButtons.Pause;

        return b;
    }
}
