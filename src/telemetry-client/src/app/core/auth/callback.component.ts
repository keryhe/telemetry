import { Component, inject, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { takeReturnUrl } from './auth-session';

/**
 * The `oidc` redirect target. The sign-in code exchange already finished in the app initializer, so
 * this only returns the user to the page they were on.
 */
@Component({
  selector: 'app-auth-callback',
  standalone: true,
  template: `<p class="signing-in">Signing in…</p>`,
  styles: [`.signing-in { padding: 48px 16px; text-align: center; opacity: 0.7; }`],
})
export class AuthCallbackComponent implements OnInit {
  private readonly router = inject(Router);

  ngOnInit(): void {
    void this.router.navigateByUrl(takeReturnUrl(), { replaceUrl: true });
  }
}
