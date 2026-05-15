import { Component, inject, Inject, OnDestroy, OnInit } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { NavbarComponent } from './shared/navbar/navbar.component';
import { StoreService } from './store/store.service';
import { MenuOffcanvasComponent } from './shared/menu-offcanvas/menu-offcanvas.component';
import { NotificationComponent } from './shared/components/notification/notification.component';
import {
  catchError,
  EMPTY,
  filter,
  forkJoin,
  of,
  Subject,
  switchMap,
  takeUntil,
} from 'rxjs';
import { ModelService } from './services/model.service';
import { ModelStore } from './store/model.store';
import { McpStore } from './store/mcp.store';
import { ConversationService } from './services/conversation.service';
import {
  MSAL_GUARD_CONFIG,
  MsalBroadcastService,
  MsalGuardConfiguration,
  MsalService,
} from '@azure/msal-angular';
import {
  RedirectRequest,
  PopupRequest,
  AuthenticationResult,
  EventMessage,
  InteractionStatus,
  EventType,
} from '@azure/msal-browser';
import { McpService } from './services/mcp.service';
import { DocumentService } from './services/document.service';
import { UserService } from './services/user.service';
import { UserStore } from './store/user.store';
import { NotificationService } from './services/notification.service';

@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    NavbarComponent,
    MenuOffcanvasComponent,
    NotificationComponent,
  ],
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.scss'],
})
export class AppComponent implements OnInit, OnDestroy {
  constructor(
    @Inject(MSAL_GUARD_CONFIG) private msalGuardConfig: MsalGuardConfiguration,
    private authService: MsalService,
    private msalBroadcastService: MsalBroadcastService,
    private storeService: StoreService,
    private conversationService: ConversationService,
    private modelService: ModelService,
    private mcpService: McpService,
    private documentService: DocumentService,
    private userService: UserService,
    private notificationService: NotificationService,
  ) {}

  private readonly modelStore = inject(ModelStore);
  private readonly mcpStore = inject(McpStore);
  private readonly userStore = inject(UserStore);

  isIframe = false;
  loginDisplay = false;
  mobileMenuOpen = false;
  private readonly _destroying$ = new Subject<void>();

  ngOnInit(): void {
    this.authService.handleRedirectObservable().subscribe();

    this.isIframe = window !== window.parent && !window.opener; // Remove this line to use Angular Universal

    this.authService.instance.enableAccountStorageEvents(); // Optional - This will enable ACCOUNT_ADDED and ACCOUNT_REMOVED events emitted when a user logs in or out of another tab or window
    this.msalBroadcastService.msalSubject$
      .pipe(
        filter(
          (msg: EventMessage) =>
            msg.eventType === EventType.ACCOUNT_ADDED ||
            msg.eventType === EventType.ACCOUNT_REMOVED,
        ),
      )
      .subscribe((result: EventMessage) => {
        if (this.authService.instance.getAllAccounts().length === 0) {
          window.location.pathname = '/';
        } else {
          this.setLoginDisplay();
        }
      });

    this.msalBroadcastService.inProgress$
      .pipe(
        filter(
          (status: InteractionStatus) => status === InteractionStatus.None,
        ),
        takeUntil(this._destroying$),
      )
      .subscribe(() => {
        this.setLoginDisplay();
        this.checkAndSetActiveAccount();
        this.loadDataIfAuthenticated();
      });
  }

  setLoginDisplay() {
    this.loginDisplay = this.authService.instance.getAllAccounts().length > 0;
  }

  checkAndSetActiveAccount() {
    /**
     * If no active account set but there are accounts signed in, sets first account to active account
     * To use active account set here, subscribe to inProgress$ first in your component
     * Note: Basic usage demonstrated. Your app may require more complicated account selection logic
     */
    let activeAccount = this.authService.instance.getActiveAccount();

    if (
      !activeAccount &&
      this.authService.instance.getAllAccounts().length > 0
    ) {
      let accounts = this.authService.instance.getAllAccounts();
      this.authService.instance.setActiveAccount(accounts[0]);
    }
  }

  loadDataIfAuthenticated() {
    if (this.loginDisplay) {
      this.userService
        .createUser()
        .pipe(
          catchError(() => {
            this.notificationService.error(
              'Failed to initialize user account.',
            );
            return EMPTY;
          }),
          switchMap((user) =>
            forkJoin([
              of(user),
              this.modelService.getModels(),
              this.conversationService.searchConversations(''),
              this.mcpService.getMcpServers(),
              this.documentService.getFileExtensions(),
            ]),
          ),
        )
        .subscribe(([user, models, conversations, mcps, fileExtensions]) => {
          this.userStore.setUser(user);
          this.modelStore.setModels(models);
          this.storeService.updateMenuConversations(conversations.items);
          this.mcpStore.setMcps(mcps);
          this.storeService.fileExtensions.set(fileExtensions);
        });
    }
  }

  loginRedirect() {
    if (this.msalGuardConfig.authRequest) {
      this.authService.loginRedirect({
        ...this.msalGuardConfig.authRequest,
      } as RedirectRequest);
    } else {
      this.authService.loginRedirect();
    }
  }

  loginPopup() {
    if (this.msalGuardConfig.authRequest) {
      this.authService
        .loginPopup({ ...this.msalGuardConfig.authRequest } as PopupRequest)
        .subscribe((response: AuthenticationResult) => {
          this.authService.instance.setActiveAccount(response.account);
        });
    } else {
      this.authService
        .loginPopup()
        .subscribe((response: AuthenticationResult) => {
          this.authService.instance.setActiveAccount(response.account);
        });
    }
  }

  logout(popup?: boolean) {
    if (popup) {
      this.authService.logoutPopup({
        mainWindowRedirectUri: '/',
      });
    } else {
      this.authService.logoutRedirect();
    }
  }

  ngOnDestroy(): void {
    this._destroying$.next(undefined);
    this._destroying$.complete();
  }
}
