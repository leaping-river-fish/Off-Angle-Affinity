namespace OffAngle.Player
{
    /// <summary>
    /// Optional visual pitch source for ThirdPersonAimAnimator.
    /// Implemented by NetworkPlayerAim so the animator never imports FishNet.
    /// </summary>
    public interface IAimPitchOverride
    {
        /// <summary>
        /// When true, the animator must use <paramref name="pitch"/> and must not
        /// read a camera. When false, the animator uses live camera pitch if that
        /// camera is active in the hierarchy.
        /// </summary>
        bool TryGetPitch(out float pitch);
    }
}
