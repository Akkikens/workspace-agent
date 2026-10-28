import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';

// The backend base URL defaults to http://localhost:5080; override with
// `{ provide: API_BASE_URL, useValue: '...' }` from ./chat-api.service.
export const appConfig: ApplicationConfig = {
  providers: [provideBrowserGlobalErrorListeners()],
};
