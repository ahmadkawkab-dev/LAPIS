import { useId, type InputHTMLAttributes } from 'react';

export function Field({ label, hint, error, id, ...props }: InputHTMLAttributes<HTMLInputElement> & {
  label: string;
  hint?: string;
  error?: string;
}) {
  const generatedId = useId();
  const fieldId = id ?? generatedId;
  const description = [props['aria-describedby'], hint ? `${fieldId}-hint` : '', error ? `${fieldId}-error` : ''].filter(Boolean).join(' ');
  return (
    <div className="wk-field">
      <label htmlFor={fieldId}>{label}{props.required && <span className="wk-field-required"> (required)</span>}</label>
      <input {...props} id={fieldId} aria-describedby={description || undefined} aria-invalid={error ? true : props['aria-invalid']} />
      {hint && <small id={`${fieldId}-hint`}>{hint}</small>}
      {error && <small className="wk-error-text" id={`${fieldId}-error`} role="alert">{error}</small>}
    </div>
  );
}
