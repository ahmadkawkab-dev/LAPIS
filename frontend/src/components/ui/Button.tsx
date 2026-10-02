import { forwardRef, type ButtonHTMLAttributes } from 'react';

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'primary' | 'secondary' | 'quiet' | 'danger';
  size?: 'default' | 'compact';
};

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'primary', size = 'default', className = '', type = 'button', ...props }, ref,
) {
  return <button ref={ref} type={type} className={`wk-button wk-button--${variant} wk-button--${size} ${className}`.trim()} {...props} />;
});

export function IconButton({ label, ...props }: ButtonProps & { label: string }) {
  return <Button variant="quiet" size="compact" {...props} className={`wk-icon-button ${props.className ?? ''}`.trim()} aria-label={label} />;
}
