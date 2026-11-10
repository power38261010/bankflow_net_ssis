import { computed, inject, Injectable, signal } from "@angular/core";
import { HttpClient, HttpErrorResponse, HttpInterceptorFn } from "@angular/common/http";
import { CanActivateFn, Router } from "@angular/router";
import { catchError, firstValueFrom, throwError } from "rxjs";
import { MatDialog } from "@angular/material/dialog";

interface User { id: string; email: string; roles: string[]; }

@Injectable({ providedIn: "root" })
export class Session {
	private readonly http = inject(HttpClient);
	readonly user = signal<User | null>(null);
	readonly isOperator = computed(() => this.user()?.roles.includes("Operator") ?? false);
	readonly isExecutive = computed(() => this.user()?.roles.includes("Executive") ?? false);
	readonly roleLabel = computed(() => this.isOperator() ? "Operador" : this.isExecutive() ? "Dirección" : "Cliente");
	async restore(): Promise<boolean> {
		try {
			this.user.set(await firstValueFrom(this.http.get<User>("/api/auth/me")));
			await this.csrf();
			return true;
		} catch {
			this.user.set(null);
			return false;
		}
	}
	csrf() { return firstValueFrom(this.http.get<void>("/api/auth/csrf")); }
	async login(username: string, password: string) {
		await this.csrf();
		const response = await firstValueFrom(this.http.post<{ requiresMfa?: boolean } | null>("/api/auth/login", { username, password }));
		if (response?.requiresMfa) {
			this.user.set(null);
			await this.csrf();
			return true;
		}
		if (!await this.restore()) throw new Error("No se pudo recuperar la sesion.");
		return false;
	}
	pendingMfa() { return firstValueFrom(this.http.get<{ pending: boolean }>("/api/auth/mfa/pending")); }
	async verifyMfa(code: string, recovery = false) {
		await this.csrf();
		await firstValueFrom(this.http.post<void>("/api/auth/mfa/verify", recovery ? { recoveryCode: code } : { code }));
		if (!await this.restore()) throw new Error("No se pudo recuperar la sesion.");
	}
	async cancelMfa() {
		await this.csrf();
		await firstValueFrom(this.http.post<void>("/api/auth/mfa/cancel", {}));
	}
	async logout() {
		try { await firstValueFrom(this.http.post<void>("/api/auth/logout", {})); }
		catch (error) { if (!(error instanceof HttpErrorResponse) || error.status !== 401) throw error; }
		this.user.set(null);
	}
}

export const sessionGuard: CanActivateFn = async () => {
	const session = inject(Session);
	const router = inject(Router);
	if (!await session.restore()) return router.createUrlTree(["/login"]);
	if (session.isExecutive()) return router.createUrlTree(["/backoffice"]);
	return session.user()?.roles.some(role => role === "Operator" || role === "Client")
		? true
		: router.createUrlTree(["/login"]);
};

export const backofficeGuard: CanActivateFn = async () => {
	const session = inject(Session);
	const router = inject(Router);
	if (!await session.restore()) return router.createUrlTree(["/login"]);
	return session.user()?.roles.some(role => role === "Operator" || role === "Executive")
		? true
		: router.createUrlTree(["/"]);
};

export const bankingUserGuard: CanActivateFn = async () => {
	const session = inject(Session);
	const router = inject(Router);
	if (!await session.restore()) return router.createUrlTree(["/login"]);
	return session.user()?.roles.some(role => role === "Operator" || role === "Client") ?? router.createUrlTree(["/"]);
};

export const sessionInterceptor: HttpInterceptorFn = (request, next) => {
	const session = inject(Session);
	const router = inject(Router);
	const dialogs = inject(MatDialog);
	return next(request).pipe(catchError((error: HttpErrorResponse) => {
		if (error.status === 401 && request.url.startsWith("/api/") && !request.url.startsWith("/api/auth/")) {
			session.user.set(null);
			dialogs.closeAll();
			void router.navigateByUrl("/login");
		}
		return throwError(() => error);
	}));
};

export function errorMessage(error: unknown): string {
	if (error instanceof HttpErrorResponse) {
		if (error.status === 0) return "No se puede conectar con BankFlow. Intentalo de nuevo.";
		if (error.status === 401) return "Credenciales incorrectas o acceso bloqueado temporalmente.";
		if (error.status === 403) return "Tu usuario no tiene permisos para esta operacion.";
		if (error.status === 404) return "La cuenta no esta disponible.";
		if (error.status === 429) return "Demasiados intentos. Espera un minuto antes de continuar.";
		if (error.status === 409) return "Operacion rechazada: saldo insuficiente o cuenta ya existente.";
		if (error.status === 400) return "Comprueba los datos. Si tu sesion ha caducado, vuelve a iniciar sesion.";
	}
	return "No se pudo completar la operacion. Intentalo de nuevo.";
}