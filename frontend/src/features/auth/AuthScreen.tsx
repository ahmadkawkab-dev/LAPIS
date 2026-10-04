import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ArrowRight, Eye, EyeOff } from 'lucide-react';
import { AuthApiError, login, register, startGoogleLogin, type AuthSession } from '../../auth';
import { errorMessage } from '../../api';
import { Wordmark } from '../../components/brand/Wordmark';
import { Button, IconButton } from '../../components/ui/Button';
import { Field } from '../../components/ui/Field';
import { ThemeControl } from '../../components/ui/ThemeControl';

export function AuthScreen({ mode, error: initialError, onSuccess, navigate }: {
  mode: 'login' | 'register';
  error: string;
  onSuccess: (session: AuthSession) => void;
  navigate: (path: string) => void;
}) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState(initialError);
  const [busy, setBusy] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<{ email?: string; password?: string }>({});
  const errorSummary = useRef<HTMLParagraphElement>(null);
  const form = useRef<HTMLFormElement>(null);
  useEffect(() => { setError(initialError); setFieldErrors({}); setShowPassword(false); }, [initialError, mode]);
  useEffect(() => { if (error) (form.current?.querySelector<HTMLInputElement>('[aria-invalid="true"]') ?? errorSummary.current)?.focus(); }, [error, fieldErrors]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError('');
    setFieldErrors({});
    try {
      const session = await (mode === 'login' ? login : register)(email.trim(), password);
      onSuccess(session);
    } catch (cause) {
      setError(errorMessage(cause));
      if (cause instanceof AuthApiError) {
        if (cause.code === 'email_already_registered') setFieldErrors({ email: errorMessage(cause) });
        else if (mode === 'register' && cause.details.length && cause.details.every((detail) => /^Passwords?\b/i.test(detail)))
          setFieldErrors({ password: cause.details.join(' ') });
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="wk-auth">
      <header className="wk-auth-header">
        <Wordmark />
        <span className="wk-arabic" lang="ar" dir="rtl">وُكنة</span>
      </header>
      <section className="wk-auth-content" aria-labelledby="auth-title">
        <div className="wk-auth-intro">
          <p className="wk-eyebrow">Your own place</p>
          <h1 id="auth-title">{mode === 'login' ? 'Good to see you.' : 'Welcome to Wukna.'}</h1>
          <p>A space that grows with you.</p>
        </div>
        <div className="wk-auth-card">
          <h2>{mode === 'login' ? 'Return to your space' : 'Make room for what matters'}</h2>
          {error && !Object.values(fieldErrors).some(Boolean) && <p ref={errorSummary} tabIndex={-1} className="wk-alert" role="alert" id="auth-error">{error}</p>}
          <form ref={form} onSubmit={submit} aria-busy={busy} aria-describedby={error && !Object.values(fieldErrors).some(Boolean) ? 'auth-error' : undefined}>
            <Field label="Email address" name="email" type="email" autoComplete="username" autoCapitalize="none" spellCheck={false}
              value={email} error={fieldErrors.email} onChange={(event) => setEmail(event.target.value)} required disabled={busy} />
            <div className="wk-password-field"><Field label="Password" name="password" type={showPassword ? 'text' : 'password'} autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
              value={password} onChange={(event) => setPassword(event.target.value)} required disabled={busy}
              error={fieldErrors.password} minLength={mode === 'register' ? 8 : undefined}
              hint={mode === 'register' ? 'Use 8 or more characters, including uppercase, lowercase, a number and a symbol.' : undefined} />
              <IconButton className="wk-password-toggle" label={showPassword ? 'Hide password' : 'Show password'}
                aria-pressed={showPassword} disabled={busy} onClick={() => setShowPassword((value) => !value)}>
                {showPassword ? <EyeOff size={18} aria-hidden="true" /> : <Eye size={18} aria-hidden="true" />}
              </IconButton></div>
            <Button type="submit" loading={busy} loadingLabel={mode === 'login' ? 'Signing in…' : 'Creating…'} disabled={!email.trim() || !password}>
              {mode === 'login' ? 'Sign in' : 'Create account'}<ArrowRight size={18} aria-hidden="true" />
            </Button>
          </form>
          {mode === 'register' && <p className="wk-auth-consent">
            By creating an account, you agree to our <a href="/terms">terms of service</a> and
            acknowledge our <a href="/privacy">privacy policy</a>.
          </p>}
          <div className="wk-auth-divider"><span>or</span></div>
          <Button variant="secondary" onClick={startGoogleLogin} disabled={busy}>Continue with Google</Button>
          <p className="wk-auth-switch">
            {mode === 'login' ? 'New to Wukna?' : 'Already have a space?'}{' '}
            <button type="button" disabled={busy} onClick={() => { setPassword(''); navigate(mode === 'login' ? '/register' : '/login'); }}>
              {mode === 'login' ? 'Create an account' : 'Sign in'}
            </button>
          </p>
        </div>
        <p className="wk-auth-caption">A thought, a plan, a place to begin.</p>
      </section>
      <footer className="wk-auth-footer">
        <span>Wukna by Hushframe</span>
        <nav className="wk-auth-legal" aria-label="Legal links">
          <a href="/privacy">Privacy policy</a>
          <a href="/terms">Terms of service</a>
        </nav>
        <ThemeControl />
      </footer>
    </main>
  );
}
