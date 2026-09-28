import { Injectable, inject } from '@angular/core';
import { Auth } from './auth';
import {
  Access,
  Alarm,
  AlarmDefinition,
  AlarmEvent,
  DeviceTemplate,
  DriverShape,
  Edge,
  LoginResponse,
  Site,
  SiteRole,
  SiteTree,
  TagHistory,
  TemplateTag,
} from './models';

/** An error carrying the message the gateway gave, so the UI can show the real reason. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly auth = inject(Auth);

  // ---- session ------------------------------------------------------------

  async login(username: string, password: string): Promise<LoginResponse> {
    const response = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    });

    if (!response.ok) {
      throw new ApiError(await this.reasonFrom(response), response.status);
    }

    return (await response.json()) as LoginResponse;
  }

  me(): Promise<Access> {
    return this.get<Access>('/api/auth/me');
  }

  logout(): Promise<unknown> {
    return this.send('POST', '/api/auth/logout', null);
  }

  // ---- browsing -----------------------------------------------------------

  drivers(): Promise<DriverShape[]> {
    return this.get<DriverShape[]>('/api/drivers');
  }

  sites(): Promise<Site[]> {
    return this.get<Site[]>('/api/sites');
  }

  tree(siteId: string): Promise<SiteTree> {
    return this.get<SiteTree>(`/api/sites/${siteId}/tree`);
  }

  history(tagId: string, from: Date, to: Date): Promise<TagHistory> {
    const query = `from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`;
    return this.get<TagHistory>(`/api/tags/${tagId}/history?${query}`);
  }

  writeTag(tagId: string, value: number | boolean | string): Promise<unknown> {
    return this.send('POST', `/api/tags/${tagId}/value`, { value });
  }

  // ---- configuration (Admin) ----------------------------------------------

  createFolder(siteId: string, body: { name: string; parentFolderId: string | null }): Promise<unknown> {
    return this.send('POST', `/api/sites/${siteId}/folders`, body);
  }

  saveDevice(siteId: string, deviceId: string | null, body: unknown): Promise<unknown> {
    return deviceId === null
      ? this.send('POST', `/api/sites/${siteId}/devices`, body)
      : this.send('PUT', `/api/sites/${siteId}/devices/${deviceId}`, body);
  }

  saveTag(deviceId: string, tagId: string | null, body: unknown): Promise<unknown> {
    return tagId === null
      ? this.send('POST', `/api/devices/${deviceId}/tags`, body)
      : this.send('PUT', `/api/devices/${deviceId}/tags/${tagId}`, body);
  }

  templates(): Promise<DeviceTemplate[]> {
    return this.get<DeviceTemplate[]>('/api/templates');
  }

  templateTags(templateId: string): Promise<TemplateTag[]> {
    return this.get<TemplateTag[]>(`/api/templates/${templateId}/tags`);
  }

  createTemplate(name: string): Promise<unknown> {
    return this.send('POST', '/api/templates', { name });
  }

  addTemplateTag(templateId: string, body: unknown): Promise<{ instancesUpdated: number }> {
    return this.send('POST', `/api/templates/${templateId}/tags`, body) as Promise<{
      instancesUpdated: number;
    }>;
  }

  deleteTemplateTag(templateId: string, tagId: string): Promise<{ instancesUpdated: number }> {
    return this.send('DELETE', `/api/templates/${templateId}/tags/${tagId}`, null) as Promise<{
      instancesUpdated: number;
    }>;
  }

  instantiate(siteId: string, body: unknown): Promise<unknown> {
    return this.send('POST', `/api/sites/${siteId}/devices/from-template`, body);
  }

  alarmsOf(tagId: string): Promise<AlarmDefinition[]> {
    return this.get<AlarmDefinition[]>(`/api/tags/${tagId}/alarms`);
  }

  saveAlarm(
    tagId: string,
    definitionId: string | null,
    body: { highLimit: number | null; lowLimit: number | null },
  ): Promise<unknown> {
    return definitionId === null
      ? this.send('POST', `/api/tags/${tagId}/alarms`, body)
      : this.send('PUT', `/api/tags/${tagId}/alarms/${definitionId}`, body);
  }

  deleteAlarm(tagId: string, definitionId: string): Promise<unknown> {
    return this.send('DELETE', `/api/tags/${tagId}/alarms/${definitionId}`, null);
  }

  deleteFolder(siteId: string, folderId: string): Promise<unknown> {
    return this.send('DELETE', `/api/sites/${siteId}/folders/${folderId}`, null);
  }

  deleteDevice(siteId: string, deviceId: string): Promise<unknown> {
    return this.send('DELETE', `/api/sites/${siteId}/devices/${deviceId}`, null);
  }

  deleteTag(deviceId: string, tagId: string): Promise<unknown> {
    return this.send('DELETE', `/api/devices/${deviceId}/tags/${tagId}`, null);
  }

  // ---- edges (Admin) ------------------------------------------------------

  /**
   * The deployment's edges (ADR-0019). Tenant-wide rather than per-Site, and Admin-only: an
   * edge's name is the identity in its certificate and its segment of the broker's topic
   * namespace, neither of which a Site scopes (ADR-0011).
   */
  edges(): Promise<Edge[]> {
    return this.get<Edge[]>('/api/edges');
  }

  saveEdge(edgeId: string | null, body: { name: string; linkDeviceId: string | null }): Promise<unknown> {
    return edgeId === null
      ? this.send('POST', '/api/edges', body)
      : this.send('PUT', `/api/edges/${edgeId}`, body);
  }

  deleteEdge(edgeId: string): Promise<unknown> {
    return this.send('DELETE', `/api/edges/${edgeId}`, null);
  }

  // ---- alarms (Operator) --------------------------------------------------

  /** The journal, newest first. The Gateway filters it to the caller's Sites (ADR-0011). */
  journal(limit = 200): Promise<AlarmEvent[]> {
    return this.get<AlarmEvent[]>(`/api/alarms/journal?limit=${limit}`);
  }

  alarms(): Promise<Alarm[]> {
    return this.get<Alarm[]>('/api/alarms');
  }

  acknowledge(definitionId: string): Promise<unknown> {
    return this.send('POST', `/api/alarms/${definitionId}/acknowledge`, null);
  }

  /** Shelves an alarm; the gateway refuses a shelf with no end, or longer than its maximum. */
  shelve(definitionId: string, durationMinutes: number): Promise<unknown> {
    return this.send('POST', `/api/alarms/${definitionId}/shelve`, { durationMinutes });
  }

  // ---- users (Admin) ------------------------------------------------------

  users(): Promise<Access[]> {
    return this.get<Access[]>('/api/users');
  }

  createUser(body: { username: string; password: string; isAdmin: boolean }): Promise<unknown> {
    return this.send('POST', '/api/users', body);
  }

  deactivateUser(userId: string): Promise<unknown> {
    return this.send('DELETE', `/api/users/${userId}`, null);
  }

  setAdmin(userId: string, isAdmin: boolean): Promise<unknown> {
    return this.send('PUT', `/api/users/${userId}/admin`, { isAdmin });
  }

  setPassword(userId: string, password: string): Promise<unknown> {
    return this.send('PUT', `/api/users/${userId}/password`, { password });
  }

  setSiteRole(userId: string, siteId: string, role: SiteRole): Promise<unknown> {
    return this.send('PUT', `/api/users/${userId}/sites/${siteId}`, { role });
  }

  removeSiteRole(userId: string, siteId: string): Promise<unknown> {
    return this.send('DELETE', `/api/users/${userId}/sites/${siteId}`, null);
  }

  // ---- transport ----------------------------------------------------------

  // Every path is relative: the gateway serves this client, so the API and the hub are on
  // the page's own origin (Phase 6). `ng serve` forwards them to a local gateway through
  // proxy.conf.json.
  private async get<T>(path: string): Promise<T> {
    const response = await fetch(path, { headers: this.headers(false) });
    await this.ensureOk(response);
    return (await response.json()) as T;
  }

  private async send(method: string, path: string, body: unknown): Promise<unknown> {
    const response = await fetch(path, {
      method,
      headers: this.headers(body !== null),
      body: body === null ? undefined : JSON.stringify(body),
    });

    await this.ensureOk(response);
    return response.status === 204 ? null : await response.json();
  }

  private headers(json: boolean): Record<string, string> {
    const headers: Record<string, string> = {};
    const token = this.auth.token();

    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    if (json) {
      headers['Content-Type'] = 'application/json';
    }

    return headers;
  }

  private async ensureOk(response: Response): Promise<void> {
    if (response.ok) {
      return;
    }

    // A 401 means the session is over — expired, revoked, or the user deactivated.
    // Forgetting it here returns the operator to the login screen, instead of leaving every
    // later request to fail the same way with nothing to explain why.
    if (response.status === 401) {
      this.auth.end();
    }

    throw new ApiError(await this.reasonFrom(response), response.status);
  }

  /**
   * The gateway answers a refused request with the rule that was broken — a cross-site
   * placement, a missing role. Surfacing that beats replacing it with a generic failure.
   */
  private async reasonFrom(response: Response): Promise<string> {
    try {
      const body = (await response.json()) as { error?: string };
      if (body?.error) {
        return body.error;
      }
    } catch {
      // Not every failure carries a JSON body; fall through to the status text.
    }

    return `${response.status} ${response.statusText}`;
  }
}
