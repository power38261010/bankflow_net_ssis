import { DestroyRef, Injectable, inject, signal } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { firstValueFrom, interval } from "rxjs";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { HubConnection, HubConnectionBuilder, HubConnectionState, HttpTransportType } from "@microsoft/signalr";
import { Router } from "@angular/router";

export interface NotificationItem { id: string; type: string; payload: string; createdAt: string; readAt: string | null; }
interface NotificationPage { items: NotificationItem[]; totalCount: number; unreadCount: number; }
export interface TransferNotification { id: string; amount: number; counterpartyLastFour: string; createdAt: string; }

@Injectable({ providedIn: "root" })
export class NotificationFeed {
	private readonly http = inject(HttpClient);
	private readonly destroyRef = inject(DestroyRef);
	private readonly router = inject(Router);
	private connection?: HubConnection;
	private starting?: Promise<void>;
	readonly items = signal<NotificationItem[]>([]);
	readonly unread = signal(0);
	readonly busy = signal(false);
	readonly error = signal("");
	readonly realtimeConnected = signal(false);

	constructor() {
		interval(30000).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
			if (this.router.url !== "/login") { if (this.connection?.state !== HubConnectionState.Connected) void this.connect(); void this.refresh(); }
		});
	}

	async connect() {
		if (!this.connection) {
			this.connection = new HubConnectionBuilder().withUrl("/hubs/notifications", {
				transport: HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents
			}).withAutomaticReconnect().build();
			this.connection.on("notificationAvailable", () => void this.refresh());
			this.connection.onreconnected(() => void this.refresh());
			this.connection.onreconnected(() => this.realtimeConnected.set(true));
			this.connection.onclose(() => this.realtimeConnected.set(false));
		}
		if (this.connection.state === HubConnectionState.Connected || this.starting) return this.starting;
		this.starting = this.connection.start().then(() => { this.realtimeConnected.set(true); return this.refresh(); })
			.catch(() => { this.realtimeConnected.set(false); return this.refresh(); }).finally(() => this.starting = undefined);
		return this.starting;
	}

	async refresh() {
		if (this.busy()) return;
		this.busy.set(true); this.error.set("");
		try {
			const page = await firstValueFrom(this.http.get<NotificationPage>("/api/notifications", { params: { page: 1, pageSize: 20 } }));
			this.items.set(page.items); this.unread.set(page.unreadCount);
		} catch { this.error.set("No se pudieron actualizar las notificaciones."); }
		finally { this.busy.set(false); }
	}

	async markRead(notification: NotificationItem) {
		if (notification.readAt) return;
		try {
			await firstValueFrom(this.http.post<void>("/api/notifications/read", { id: notification.id }));
			this.items.update(items => items.map(item => item.id === notification.id ? { ...item, readAt: new Date().toISOString() } : item));
			this.unread.update(count => Math.max(0, count - 1));
		} catch { this.error.set("No se pudo actualizar esta notificacion."); }
	}

	transfer(item: NotificationItem): TransferNotification | null {
		if (item.type !== "TransferSent" && item.type !== "TransferReceived") return null;
		try {
			const payload = JSON.parse(item.payload);
			if (typeof payload.id !== "string" || typeof payload.amount !== "number" || typeof payload.counterpartyLastFour !== "string") return null;
			return payload as TransferNotification;
		} catch { return null; }
	}

	async disconnect() {
		if (this.connection?.state === HubConnectionState.Connected) await this.connection.stop();
		this.connection = undefined;
	}
}