import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { ChatApiService, FALLBACK_USERS } from './chat-api.service';
import { ChatComponent } from './chat.component';
import { DemoUser } from './events';

@Component({
  selector: 'app-root',
  imports: [ChatComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header>
      <div class="brand">
        <h1>Workspace Agent</h1>
        @if (backendDown()) {
          <span class="offline" title="GET /api/users failed; using the built-in demo users">backend offline</span>
        }
      </div>
      <label class="user">
        <span>Signed in as</span>
        <select
          aria-label="Demo user"
          [value]="api.userId()"
          [disabled]="chat()?.streaming()"
          (change)="selectUser($any($event.target).value)"
        >
          @for (u of users(); track u.id) {
            <option [value]="u.id" [selected]="u.id === api.userId()">{{ u.name }}</option>
          }
        </select>
      </label>
      @if (currentUser(); as u) {
        <span class="role">{{ u.role }} · {{ u.businessUnit ?? 'all business units' }}</span>
      }
    </header>
    <main>
      <app-chat />
    </main>
  `,
  styles: `
    :host { display: flex; flex-direction: column; height: 100dvh; }
    header { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 16px; padding: 10px 16px;
      border-bottom: 1px solid var(--border); background: var(--surface); }
    .brand { display: flex; align-items: center; gap: 8px; margin-right: auto; }
    h1 { font-size: 16px; margin: 0; }
    .offline { font-size: 11px; padding: 2px 8px; border-radius: 999px; background: #fbe0e0; color: #8a1f1f; }
    .user { display: flex; align-items: center; gap: 6px; font-size: 13px; color: var(--muted); }
    select { font: inherit; color: var(--text); padding: 4px 6px; border-radius: 6px; border: 1px solid var(--border); background: var(--surface); }
    .role { font-size: 12px; color: var(--muted); }
    main { flex: 1; min-height: 0; display: flex; flex-direction: column; width: 100%; max-width: 860px; margin: 0 auto; padding: 0 16px; box-sizing: border-box; }
  `,
})
export class AppComponent {
  protected readonly api = inject(ChatApiService);
  protected readonly chat = viewChild(ChatComponent);
  protected readonly users = signal<DemoUser[]>(FALLBACK_USERS);
  protected readonly backendDown = signal(false);
  protected readonly currentUser = computed(() => this.users().find((u) => u.id === this.api.userId()));

  constructor() {
    this.api.users().then(
      (users) => {
        if (Array.isArray(users) && users.length) this.users.set(users);
      },
      () => this.backendDown.set(true),
    );
  }

  protected selectUser(id: string): void {
    if (id === this.api.userId()) return;
    this.api.userId.set(id);
    // Sessions belong to a user; start a fresh conversation under the new identity.
    this.chat()?.reset();
  }
}
