---
name: Admin email notifications (Resend)
description: How platform admin emails are sent and Resend test-mode limits
---

- AdminEmailSender picks provider at send time: RESEND_API_KEY → Resend, else SMTP_HOST → SMTP, else logs + returns false. Recipient: ADMIN_NOTIFY_EMAIL (default admin@delicatecourier.co.za).
- **Resend test mode**: without a verified domain at resend.com/domains, Resend returns 403 and only delivers to the account owner's own address (here ashleybaloyi.work@gmail.com). ADMIN_NOTIFY_EMAIL is currently set to that Gmail as a workaround; to send to admin@delicatecourier.co.za, verify the domain and set RESEND_FROM to an address on it.
- **How to apply:** any new outbound email feature must go through IAdminEmailSender or account for the same domain-verification limits.
