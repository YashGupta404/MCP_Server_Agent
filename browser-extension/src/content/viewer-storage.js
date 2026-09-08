// Grants the embedded CIC viewer access to its first-party cookies while it runs
// inside the extension side-panel iframe (Storage Access API). Without this, the
// viewer's session cookie isn't sent in the third-party iframe context, so the SPA
// renders once and then errors ("Error during processing document") on its next
// authenticated call.
(async () => {
  // Only act when we're actually embedded in a parent frame.
  if (window.top === window.self) return;

  try {
    if (await document.hasStorageAccess()) return; // already granted — nothing to do
  } catch {
    return; // API unavailable
  }

  // Requests access; on success reload so the now-authorized cookie is attached to
  // every subsequent viewer API call. Returns true when access was granted.
  const tryGrant = async () => {
    try {
      await document.requestStorageAccess();
      location.reload();
      return true;
    } catch {
      return false;
    }
  };

  // Chrome may grant silently when the user recently used the site first-party
  // (e.g. just signed in via the viewer window/tab).
  if (await tryGrant()) return;

  // Otherwise the grant needs a user gesture inside the frame — do it on first click.
  const onGesture = async () => {
    window.removeEventListener("pointerdown", onGesture, true);
    await tryGrant();
  };
  window.addEventListener("pointerdown", onGesture, true);
})();
