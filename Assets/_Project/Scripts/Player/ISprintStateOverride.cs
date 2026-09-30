namespace OffAngle.Player
{
    /// <summary>
    /// Optional visual sprint source for ThirdPersonLocomotionAnimator.
    /// Implemented by NetworkPlayerLocomotionSync so the animator never imports FishNet.
    /// </summary>
    public interface ISprintStateOverride
    {
        /// <summary>
        /// When true, the animator must use <paramref name="sprinting"/> instead of
        /// reading the local MovementStateMachine (which is disabled on remotes).
        /// When false, the animator falls back to the local state machine.
        /// </summary>
        bool TryGetSprinting(out bool sprinting);
    }
}
