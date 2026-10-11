import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AuthService } from '../../core/auth/auth.service';
import { UserService } from '../../core/auth/user.service';

/**
 * The header's user button and menu, shown whenever someone is signed in (OIDC or cookie mode). The avatar is the
 * user's picture, else the initial of their first name (else name, else email), else a generic person icon. The menu
 * lists who they are and offers Sign out, the only Sign out in the header.
 */
@Component({
  selector: 'app-user-menu',
  standalone: true,
  imports: [NgTemplateOutlet, MatButtonModule, MatDividerModule, MatIconModule, MatMenuModule, MatTooltipModule],
  templateUrl: './user-menu.component.html',
  styleUrl: './user-menu.component.scss',
})
export class UserMenuComponent {
  private readonly users = inject(UserService);
  protected readonly auth = inject(AuthService);

  protected readonly user = this.users.user;
  /** A picture URL that failed to load falls back to the initial. */
  private readonly brokenPicture = signal<string | null>(null);

  protected readonly picture = computed(() => {
    const p = this.user()?.picture;
    return p && p !== this.brokenPicture() ? p : null;
  });

  protected readonly initial = computed(() => {
    const u = this.user();
    const source = u?.givenName?.trim() || u?.name?.trim() || u?.email?.trim() || '';
    return source ? source.charAt(0).toUpperCase() : null;
  });

  /** The name, else the email, else a generic label. */
  protected readonly label = computed(() => this.user()?.name?.trim() || this.user()?.email?.trim() || 'Account');

  protected onPictureError(): void {
    this.brokenPicture.set(this.user()?.picture ?? null);
  }
}
