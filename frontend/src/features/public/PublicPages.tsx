import type { ReactNode } from 'react';
import { Wordmark } from '../../components/brand/Wordmark';

const contactEmail = 'ahmadkawkab.official@gmail.com';

function PublicFrame({ children }: { children: ReactNode }) {
  return (
    <div className="wk-public"><a className="wk-skip-link" href="#main-content">Skip to content</a>
      <header className="wk-public-header">
        <a href="/" aria-label="Wukna home"><Wordmark /></a>
        <a className="wk-public-header-link" href="/login">Open Wukna</a>
      </header>
      <main id="main-content" tabIndex={-1}>{children}</main>
      <footer className="wk-public-footer">
        <span>Wukna by Hushframe</span>
        <nav aria-label="Legal links">
          <a href="/privacy">Privacy policy</a>
          <a href="/terms">Terms of service</a>
          <a href={`mailto:${contactEmail}`}>Contact</a>
        </nav>
      </footer>
    </div>
  );
}

export function PublicHome() {
  return (
    <PublicFrame>
      <div className="wk-public-home">
        <p className="wk-public-eyebrow">A place for ideas to grow</p>
        <h1>Make room for what matters.</h1>
        <p className="wk-public-lead">
          Wukna is a visual workspace for gathering notes, planning tasks, and connecting ideas on boards.
          Keep a board to yourself or invite other Wukna members to work on it with you in real time.
        </p>
        <div className="wk-public-actions">
          <a className="wk-public-button wk-public-button-primary" href="/register">Create an account</a>
          <a className="wk-public-button wk-public-button-secondary" href="/login">Sign in</a>
        </div>
        <section className="wk-public-home-note" aria-labelledby="wk-public-google-title">
          <h2 id="wk-public-google-title">Sign in with Google, if you prefer.</h2>
          <p>
            Google sign-in uses your verified email address and Google account identifier to create or access
            your Wukna account. Wukna does not request access to your Gmail, Drive, or Calendar.
          </p>
          <p>
            Learn how your information is handled in our <a href="/privacy">privacy policy</a> and read the
            <a href="/terms"> terms of service</a> before creating an account.
          </p>
        </section>
      </div>
    </PublicFrame>
  );
}

function LegalHeader({ title, summary }: { title: string; summary: string }) {
  return (
    <header className="wk-legal-heading">
      <p className="wk-public-eyebrow">Wukna · Hushframe</p>
      <h1>{title}</h1>
      <p>{summary}</p>
      <p className="wk-legal-date">Last updated: September 27, 2026</p>
    </header>
  );
}

export function PrivacyPolicyPage() {
  return (
    <PublicFrame>
      <article className="wk-legal">
        <LegalHeader title="Privacy policy" summary="How Hushframe collects, uses, and protects information when you use Wukna." />

        <section>
          <h2>Who operates Wukna</h2>
          <p>Hushframe operates Wukna. For privacy questions or requests, email <a href={`mailto:${contactEmail}`}>{contactEmail}</a>.</p>
        </section>

        <section>
          <h2>Information we collect</h2>
          <ul>
            <li><strong>Account information:</strong> your email address, username, optional display name, password hash if you use a password, and optional profile photo.</li>
            <li><strong>Google sign-in information:</strong> when you choose Google, we receive your verified email address and a Google account identifier to establish or link your Wukna account. We do not request access to Gmail, Drive, or Calendar, and we do not retain Google OAuth access or refresh tokens.</li>
            <li><strong>Workspace content:</strong> boards, notes, tasks, connections, and membership or sharing settings you create.</li>
            <li><strong>Technical information:</strong> our hosting and security providers may process IP addresses, request times, URLs, browser details, and error or security logs when you use the site.</li>
          </ul>
        </section>

        <section>
          <h2>How we use information</h2>
          <p>We use this information to create and secure accounts, authenticate you, show and synchronize your boards, enable collaboration, store your profile photo, respond to support requests, diagnose problems, and protect the service from abuse. We do not use your Google sign-in information to access other Google products or sell your personal information.</p>
        </section>

        <section>
          <h2>Cookies and browser storage</h2>
          <p>Wukna uses essential cookies for sign-in, Google sign-in, and request protection. The browser may store your theme preference locally and temporary editing drafts for the current session. These are used to make the service work, not for advertising. You can clear browser storage through your browser settings, though this may sign you out or discard an unfinished draft.</p>
        </section>

        <section>
          <h2>Sharing and service providers</h2>
          <p>Members you invite to a board can see its content and the profile details used to identify collaborators. Profile photos are served through a public image URL; avoid uploading a photo you do not want accessible to someone with that URL. We use infrastructure providers, including Oracle Cloud for hosting and Cloudflare for DNS, security, and traffic delivery. Google processes the sign-in you initiate under its own policies. We share information with these providers only as needed to operate Wukna, or when required by law.</p>
        </section>

        <section>
          <h2>Storage, retention, and security</h2>
          <p>Application data is hosted on an Oracle Cloud server in Saudi Arabia; Cloudflare and Google may process sign-in or traffic data in other locations. We keep account and workspace data while your account is active or until deletion is requested. Security logs and backup copies may remain for operational, security, or legal reasons until their normal rotation or required retention ends. We use HTTPS, access controls, and other safeguards, but no online service can guarantee absolute security.</p>
        </section>

        <section>
          <h2>Your choices and requests</h2>
          <p>You can update your profile, remove a profile photo, delete boards you own, and disconnect Google sign-in when another sign-in method is available. To request a copy, correction, or deletion of your account data, email <a href={`mailto:${contactEmail}`}>{contactEmail}</a> from the address associated with your account. We may need to verify your identity and retain limited information when required for security or legal reasons.</p>
        </section>

        <section>
          <h2>Changes to this policy</h2>
          <p>We may update this policy as Wukna changes. The date at the top shows the latest version. Material changes will be communicated through the service or another appropriate channel.</p>
        </section>
      </article>
    </PublicFrame>
  );
}

export function TermsOfServicePage() {
  return (
    <PublicFrame>
      <article className="wk-legal">
        <LegalHeader title="Terms of service" summary="The conditions for creating an account and using Wukna." />

        <section>
          <h2>Using Wukna</h2>
          <p>Wukna is a visual workspace operated by Hushframe. By creating an account or using the service, you agree to these terms. If you do not agree, do not use the service. You must be able to enter into this agreement under the laws that apply to you.</p>
        </section>

        <section>
          <h2>Your account</h2>
          <p>Provide accurate account information and keep your sign-in credentials secure. You are responsible for activity under your account. You may sign in with a password or Google, and you should keep at least one working sign-in method. Contact us promptly if you believe your account has been accessed without permission.</p>
        </section>

        <section>
          <h2>Your content and collaboration</h2>
          <p>You retain ownership of the boards, notes, images, and other content you add. You give Hushframe permission to store, process, display, and transmit that content as needed to operate Wukna and the sharing settings you choose. Only share content you have the right to use. People you invite to a board may view it and, if you grant edit access, change its content. You are responsible for deciding whom to invite.</p>
        </section>

        <section>
          <h2>Acceptable use</h2>
          <p>Do not use Wukna to break the law, infringe others' rights, harass others, distribute malicious material, attempt unauthorized access, disrupt the service, or abuse other users' accounts or data. We may restrict access or remove content when reasonably necessary to protect users, the service, or legal rights.</p>
        </section>

        <section>
          <h2>Availability and changes</h2>
          <p>We may maintain, change, or discontinue features. We aim to keep Wukna available, but we do not promise uninterrupted service or permanent storage. Keep independent copies of content that is important to you. The service is provided as available, subject to any rights that cannot be excluded by law.</p>
        </section>

        <section>
          <h2>Third-party services and privacy</h2>
          <p>Google sign-in is optional and is subject to Google's terms and privacy practices. Infrastructure providers help deliver Wukna. Our <a href="/privacy">privacy policy</a> explains how Hushframe handles your information.</p>
        </section>

        <section>
          <h2>Ending use and contacting us</h2>
          <p>You may stop using Wukna at any time. To request account deletion or ask about these terms, email <a href={`mailto:${contactEmail}`}>{contactEmail}</a>. We may update these terms; the date at the top identifies the current version. Continued use after changes take effect means you accept the updated terms, where permitted by law.</p>
        </section>
      </article>
    </PublicFrame>
  );
}
