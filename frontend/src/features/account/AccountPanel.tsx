import { lazy, Suspense, useCallback, useEffect, useRef, useState } from "react";
import { Camera, Link2, LogOut, RefreshCw, Trash2, Unlink } from "lucide-react";
import {
  getAccountStatus,
  startGoogleLink,
  unlinkGoogle,
  type AccountStatus,
  type AuthSession,
} from "../../auth";
import { AuthApiError, errorMessage, profileApi, type ProfileDto } from "../../api";
import { Button } from "../../components/ui/Button";
import { Field } from "../../components/ui/Field";
import { Avatar } from "../../components/ui/Avatar";
import { ThemeControl } from "../../components/ui/ThemeControl";
import { NotificationPreferences } from "../notifications/NotificationPreferences";
const ColorStudio = lazy(() => import('../../components/appearance/ColorStudio'));

export function AccountPanel({
  user,
  section,
  navigate,
  signOut,
  signOutEverywhere,
  notify,
  onProfileUpdated,
}: {
  user: AuthSession["user"];
  section: "profile" | "preferences";
  navigate: (path: string) => void;
  signOut: () => void;
  signOutEverywhere: () => void;
  notify: (message: string) => void;
  onProfileUpdated: (profile: ProfileDto) => void;
}) {
  const [status, setStatus] = useState<AccountStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState("");
  const [profile, setProfile] = useState<ProfileDto | null>(null);
  const [profileLoading, setProfileLoading] = useState(true);
  const [username, setUsername] = useState(user.username ?? "");
  const [displayName, setDisplayName] = useState(user.displayName ?? "");
  const [profileBusy, setProfileBusy] = useState(false);
  const [profileError, setProfileError] = useState("");
  const [profileFieldError, setProfileFieldError] = useState<{ username?: string; displayName?: string }>({});
  const [profileLoadFailed, setProfileLoadFailed] = useState(false);
  const [preview, setPreview] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setFailure("");
    try {
      setStatus(await getAccountStatus());
    } catch (cause) {
      setStatus(null);
      setFailure(errorMessage(cause));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const loadProfile = useCallback(async () => {
    setProfileLoading(true);
    setProfileError("");
    setProfileLoadFailed(false);
    try {
      const value = await profileApi.get();
      const normalized: ProfileDto = {
        userId: typeof value?.userId === "string" ? value.userId : user.id,
        email: typeof value?.email === "string" ? value.email : user.email ?? "",
        username: typeof value?.username === "string" ? value.username : user.username ?? "",
        displayName: typeof value?.displayName === "string" ? value.displayName : null,
        profileImageUrl: typeof value?.profileImageUrl === "string" ? value.profileImageUrl : null,
        profileImageVersion: typeof value?.profileImageVersion === "string" ? value.profileImageVersion : null,
      };
      setProfile(normalized);
      setUsername(normalized.username);
      setDisplayName(normalized.displayName ?? "");
      onProfileUpdated(normalized);
    } catch (cause) {
      setProfileError(errorMessage(cause));
      setProfileLoadFailed(true);
    } finally {
      setProfileLoading(false);
    }
  }, [onProfileUpdated, user.displayName, user.username]);

  useEffect(() => {
    void loadProfile();
  }, [loadProfile]);

  useEffect(() => () => { if (preview) URL.revokeObjectURL(preview); }, [preview]);

  async function saveProfile(event: React.FormEvent) {
    event.preventDefault();
    setProfileBusy(true);
    setProfileError("");
    setProfileFieldError({});
    setProfileLoadFailed(false);
    try {
      const response = await profileApi.update(username, displayName);
      const updated: ProfileDto = {
        userId: typeof response?.userId === "string" ? response.userId : user.id,
        email: typeof response?.email === "string" ? response.email : user.email ?? "",
        username: typeof response?.username === "string" ? response.username : username,
        displayName: typeof response?.displayName === "string" ? response.displayName : null,
        profileImageUrl: typeof response?.profileImageUrl === "string" ? response.profileImageUrl : profile?.profileImageUrl ?? null,
        profileImageVersion: typeof response?.profileImageVersion === "string" ? response.profileImageVersion : profile?.profileImageVersion ?? null,
      };
      setProfile(updated);
      setUsername(updated.username);
      setDisplayName(updated.displayName ?? "");
      onProfileUpdated(updated);
      notify("Profile updated");
    } catch (cause) {
      const message = errorMessage(cause);
      setProfileError(message);
      if (cause instanceof AuthApiError) {
        if (['invalid_username', 'username_taken'].includes(cause.code)) setProfileFieldError({ username: message });
        if (cause.code === 'display_name_too_long') setProfileFieldError({ displayName: message });
      }
    }
    finally { setProfileBusy(false); }
  }

  async function selectImage(file?: File) {
    if (!file) return;
    setProfileError("");
    setProfileLoadFailed(false);
    if (!file.type.startsWith("image/")) { setProfileError("Choose an image file."); return; }
    if (file.size > 5 * 1024 * 1024) { setProfileError("Profile images must be 5 MB or smaller."); return; }
    const objectUrl = URL.createObjectURL(file);
    setPreview(objectUrl);
    setProfileBusy(true);
    try {
      const response = await profileApi.uploadAvatar(file);
      const updated = profile ?? await profileApi.get();
      const canonical = { ...updated, profileImageUrl: response.profileImageUrl, profileImageVersion: response.profileImageVersion };
      setProfile(canonical);
      onProfileUpdated(canonical);
      notify("Profile photo updated");
    } catch (cause) { setProfileError(errorMessage(cause)); }
    finally { setProfileBusy(false); setPreview((current) => { if (current) URL.revokeObjectURL(current); return null; }); }
  }

  async function removePhoto() {
    setProfileBusy(true);
    setProfileError("");
    setProfileLoadFailed(false);
    try {
      const response = await profileApi.removeAvatar();
      const updated = { ...(profile ?? await profileApi.get()), ...response };
      setProfile(updated);
      onProfileUpdated(updated);
      notify("Profile photo removed");
    } catch (cause) { setProfileError(errorMessage(cause)); }
    finally { setProfileBusy(false); }
  }

  async function linkGoogle() {
    try {
      await startGoogleLink();
    } catch (cause) {
      notify(errorMessage(cause));
    }
  }

  async function removeGoogle() {
    try {
      await unlinkGoogle();
      await load();
      notify("Google login removed");
    } catch (cause) {
      notify(errorMessage(cause));
    }
  }

  const externalLogins = Array.isArray(status?.externalLogins) ? status.externalLogins : [];
  const googleConnected = externalLogins.includes("Google");
  const canRemoveGoogle =
    status?.hasPassword ||
    externalLogins.some((provider) => provider !== "Google");

  return (
    <div className="wk-account-page">
      <header className="wk-account-page-heading">
        <div><span className="wk-account-eyebrow">Account</span><h1>Profile & settings</h1>
          <p>Manage how Wukna works for you.</p></div>
        {section === "profile" && <Button type="submit" form="wk-profile-form" loading={profileBusy} loadingLabel="Saving…" disabled={profileLoading || !username.trim()}>
          Save changes</Button>}
      </header>
      <div className="wk-account-layout">
      <nav className="wk-account-tabs" aria-label="Account sections">
        <button type="button" aria-current={section === "profile" ? "page" : undefined}
          onClick={() => navigate("/account/profile")}>Profile</button>
        <button type="button" aria-current={section === "preferences" ? "page" : undefined}
          onClick={() => navigate("/account/preferences")}>Preferences & security</button>
      </nav>
      <div className="wk-account-main">
      {section === "profile" && profileError && <div className="wk-account-error" role="alert">
        <p>{profileError}</p>
        {profileLoadFailed && <Button variant="secondary" onClick={() => void loadProfile()}><RefreshCw size={16} aria-hidden="true" /> Retry loading profile</Button>}
      </div>}
      <div className="wk-account-panel">
      {section === "profile" ? <>
      {profileLoading ? <div className="wk-profile-loading" role="status" aria-label="Loading profile">
        <span /><span /><span />
      </div> : <form id="wk-profile-form" className="wk-profile-form" onSubmit={(event) => void saveProfile(event)}>
        <section className="wk-account-section" aria-labelledby="wk-profile-heading">
          <h2 id="wk-profile-heading">Your details</h2>
          <div className="wk-profile-photo-row">
            <Avatar identity={{ ...user, displayName, username, profileImageUrl: preview ?? profile?.profileImageUrl }} size="large" />
            <div className="wk-profile-photo-actions">
              <input ref={fileInput} type="file" accept="image/jpeg,image/png,image/webp" hidden
                onChange={(event) => { void selectImage(event.currentTarget.files?.[0]); event.currentTarget.value = ""; }} />
              <Button variant="secondary" type="button" disabled={profileBusy} onClick={() => fileInput.current?.click()}>
                <Camera size={16} aria-hidden="true" /> Change photo
              </Button>
              {profile?.profileImageUrl && <Button variant="quiet" type="button" disabled={profileBusy} onClick={() => void removePhoto()}>
                <Trash2 size={16} aria-hidden="true" /> Remove photo
              </Button>}
            </div>
          </div>
          <Field label="Display name" name="displayName" autoComplete="name" maxLength={80}
            error={profileFieldError.displayName} value={displayName} onChange={(event) => setDisplayName(event.target.value)} disabled={profileBusy} />
          <Field label="Username" name="username" autoComplete="nickname" minLength={3} maxLength={30} required
            hint="3–30 letters, numbers, periods, underscores, or hyphens."
            error={profileFieldError.username} value={username} onChange={(event) => setUsername(event.target.value)} disabled={profileBusy} />
          <Field label="Email" name="email" type="email" value={profile?.email ?? user.email} readOnly hint="Your sign-in email cannot be changed here." />
        </section>
      </form>
      }</> : <>
      <section className="wk-account-section" aria-labelledby="wk-appearance-heading">
        <h2 id="wk-appearance-heading">Appearance</h2>
        <ThemeControl />
        <Suspense fallback={<p role="status">Opening Color Studio…</p>}><ColorStudio /></Suspense>
      </section>

      <NotificationPreferences key={user.id} userId={user.id} notify={notify} />

      <section className="wk-account-section" aria-labelledby="wk-sign-in-heading">
        <div className="wk-account-section-heading">
          <div>
            <h2 id="wk-sign-in-heading">Sign-in methods</h2>
            <p>Choose how you can return to Wukna.</p>
          </div>
          {loading && <span className="wk-account-status" role="status">Checking…</span>}
        </div>

        {failure ? (
          <div className="wk-account-error" role="alert">
            <p>{failure}</p>
            <Button variant="secondary" onClick={() => void load()}>
              <RefreshCw size={16} aria-hidden="true" /> Retry
            </Button>
          </div>
        ) : status ? (
          <div className="wk-provider-row">
            <div>
              <strong>Google</strong>
              <span>{googleConnected ? "Connected" : "Not connected"}</span>
            </div>
            {googleConnected ? (
              canRemoveGoogle ? (
                <Button variant="quiet" onClick={() => void removeGoogle()}>
                  <Unlink size={16} aria-hidden="true" /> Remove
                </Button>
              ) : (
                <span className="wk-account-status">Only sign-in method</span>
              )
            ) : (
              <Button variant="secondary" onClick={() => void linkGoogle()}>
                <Link2 size={16} aria-hidden="true" /> Connect
              </Button>
            )}
          </div>
        ) : null}
      </section>

      <section className="wk-account-section wk-account-session" aria-labelledby="wk-session-heading">
        <div>
          <h2 id="wk-session-heading">Session</h2>
          <p>Sign out here, or close every active Wukna session.</p>
        </div>
        <div className="wk-account-actions">
          <Button variant="secondary" onClick={signOut}>
            <LogOut size={16} aria-hidden="true" /> Sign out
          </Button>
          <Button variant="danger" onClick={signOutEverywhere}>
            Sign out everywhere
          </Button>
        </div>
      </section>
      </>}
      </div>
      </div>
      </div>
      <nav className="wk-account-legal" aria-label="Legal links">
        <a href="/privacy">Privacy policy</a>
        <a href="/terms">Terms of service</a>
      </nav>
      <p className="wk-tour-icon-credit">Tour icon designed by QudaDesign from <a href="https://www.flaticon.com/free-icon/question-mark-circle_10380844" target="_blank" rel="noopener noreferrer">Flaticon</a>.</p>
    </div>
  );
}
