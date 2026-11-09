import { Component, inject, signal, OnDestroy } from "@angular/core";
import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { firstValueFrom } from "rxjs";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { MatDialogModule, MatDialogRef } from "@angular/material/dialog";
import { MatFormFieldModule } from "@angular/material/form-field";
import { MatInputModule } from "@angular/material/input";
import { MatButtonModule } from "@angular/material/button";
import { MatCheckboxModule } from "@angular/material/checkbox";
import { MatIconModule } from "@angular/material/icon";
import { MatProgressBarModule } from "@angular/material/progress-bar";
import { Router } from "@angular/router";
import { toDataURL } from "qrcode";
import { Session, errorMessage } from "./session";

interface MfaStatus { enabled: boolean; recoveryCodesRemaining: number; }
interface MfaSetup { sharedKey: string; authenticatorUri: string; }
interface MfaRecovery { recoveryCodes: string[]; }

@Component({
	selector: "app-mfa-dialog",
	imports: [ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatCheckboxModule, MatIconModule, MatProgressBarModule],
	template: `
		<h2 mat-dialog-title>Seguridad de acceso</h2>
		@if (busy()) {<mat-progress-bar mode="indeterminate" aria-label="Actualizando seguridad" />}
		<form [formGroup]="form" (ngSubmit)="submit()">
			<mat-dialog-content>
				@if (codes().length) {
					<h3>Codigos de recuperacion</h3><div class="recovery-grid">@for (code of codes(); track code) {<code>{{ code }}</code>}</div>
					<mat-checkbox [checked]="saved()" (change)="saved.set($event.checked)">He guardado los codigos</mat-checkbox>
				} @else if (setup(); as setup) {
					<img class="mfa-qr" [src]="qr()" alt="QR del autenticador" width="192" height="192">
					<p class="mfa-key"><code>{{ setup.sharedKey }}</code></p>
					<mat-form-field appearance="outline"><mat-label>Codigo de autenticador</mat-label><input matInput formControlName="verification" maxlength="6" inputmode="numeric" autocomplete="one-time-code"><mat-error>Introduce 6 digitos.</mat-error></mat-form-field>
				} @else if (mode()) {
					<h3>{{ mode() === 'setup' ? 'Activar MFA' : mode() === 'disable' ? 'Desactivar MFA' : 'Renovar codigos de recuperacion' }}</h3>
					<mat-form-field appearance="outline"><mat-label>Contrasena actual</mat-label><input matInput formControlName="password" type="password" autocomplete="current-password" maxlength="256"><mat-error>Introduce tu contrasena.</mat-error></mat-form-field>
					@if (mode() !== 'setup') {
						<mat-form-field appearance="outline"><mat-label>{{ useRecovery() ? 'Codigo de recuperacion' : 'Codigo de autenticador' }}</mat-label><input matInput formControlName="verification" [attr.inputmode]="useRecovery() ? 'text' : 'numeric'" autocomplete="one-time-code" [maxlength]="useRecovery() ? 32 : 6"><mat-error>Introduce un codigo valido.</mat-error></mat-form-field>
						<mat-checkbox [checked]="useRecovery()" (change)="useRecovery.set($event.checked); form.controls.verification.reset()">Usar codigo de recuperacion</mat-checkbox>
					}
				} @else if (status(); as status) {
					<div class="mfa-status"><mat-icon>{{ status.enabled ? 'verified_user' : 'security' }}</mat-icon><div><strong>{{ status.enabled ? 'MFA activado' : 'MFA desactivado' }}</strong><p class="muted">App autenticadora @if (status.enabled) { / {{ status.recoveryCodesRemaining }} codigos de recuperacion }</p></div></div>
					@if (status.enabled) {<div class="heading-actions"><button mat-stroked-button type="button" (click)="begin('regenerate')"><mat-icon>refresh</mat-icon>Renovar codigos</button><button mat-stroked-button type="button" (click)="begin('disable')"><mat-icon>lock_open</mat-icon>Desactivar MFA</button></div>}
					@else {<button mat-flat-button type="button" (click)="begin('setup')"><mat-icon>security</mat-icon>Activar MFA</button>}
				}
				@if (error()) {<p role="alert" class="form-error">{{ error() }}</p>}
			</mat-dialog-content>
			<mat-dialog-actions align="end">
				@if (codes().length) {<button mat-stroked-button type="button" (click)="download()"><mat-icon>download</mat-icon>Guardar codigos</button><button mat-flat-button type="button" [disabled]="!saved()" (click)="finish()">Finalizar</button>}
				@else {<button mat-button type="button" [disabled]="busy()" (click)="dialog.close()">Cerrar</button>@if (mode() && !setup()) {<button mat-button type="button" [disabled]="busy()" (click)="begin(null)">Cancelar</button>}@if (mode()) {<button mat-flat-button type="submit" [disabled]="busy()">{{ busy() ? 'Procesando...' : setup() ? 'Confirmar activacion' : mode() === 'setup' ? 'Generar QR' : 'Confirmar' }}</button>}}
			</mat-dialog-actions>
		</form>
	`
})
export class MfaDialog implements OnDestroy {
	readonly dialog = inject(MatDialogRef<MfaDialog>);
	private readonly http = inject(HttpClient);
	private readonly session = inject(Session);
	private readonly router = inject(Router);
	readonly status = signal<MfaStatus | null>(null);
	readonly setup = signal<MfaSetup | null>(null);
	readonly qr = signal("");
	readonly codes = signal<string[]>([]);
	readonly saved = signal(false);
	readonly useRecovery = signal(false);
	readonly mode = signal<"setup" | "disable" | "regenerate" | null>(null);
	readonly busy = signal(false);
	readonly error = signal("");
	readonly form = inject(FormBuilder).nonNullable.group({ password: ["", Validators.maxLength(256)], verification: ["", Validators.maxLength(32)] });
	constructor() { void this.load(); }
	async load() {
		this.busy.set(true);
		try { this.status.set(await firstValueFrom(this.http.get<MfaStatus>("/api/auth/mfa"))); }
		catch (error) { this.error.set(errorMessage(error)); }
		finally { this.busy.set(false); }
	}
	begin(mode: "setup" | "disable" | "regenerate" | null) { this.mode.set(mode); this.form.reset(); this.useRecovery.set(false); this.error.set(""); }
	async submit() {
		if (this.busy() || !this.mode()) return;
		const { password, verification } = this.form.getRawValue();
		const code = verification.trim();
		if (!this.setup() && !password) { this.form.controls.password.setErrors({ required: true }); this.form.markAllAsTouched(); return; }
		if ((this.setup() || this.mode() !== "setup") && (!code || !this.useRecovery() && !/^[0-9]{6}$/.test(code))) {
			this.form.controls.verification.setErrors({ invalid: true }); this.form.markAllAsTouched(); return;
		}
		this.busy.set(true); this.dialog.disableClose = true; this.error.set("");
		try {
			if (this.setup()) {
				const result = await firstValueFrom(this.http.post<MfaRecovery>("/api/auth/mfa/enable", { code }));
				this.showRecovery(result); await this.session.restore();
			} else if (this.mode() === "setup") {
				const setup = await firstValueFrom(this.http.post<MfaSetup>("/api/auth/mfa/setup", { password }));
				this.qr.set(await toDataURL(setup.authenticatorUri, { width: 192, margin: 2 }));
				this.setup.set(setup); this.form.reset(); await this.session.csrf();
			} else {
				const proof = { password, ...(this.useRecovery() ? { recoveryCode: code } : { code }) };
				if (this.mode() === "disable") {
					await firstValueFrom(this.http.post<void>("/api/auth/mfa/disable", proof));
					this.session.user.set(null); this.dialog.close(); await this.router.navigateByUrl("/login");
				} else {
					const result = await firstValueFrom(this.http.post<MfaRecovery>("/api/auth/mfa/recovery-codes", proof));
					this.showRecovery(result); await this.session.restore();
				}
			}
		} catch (error) {
			this.error.set(error instanceof HttpErrorResponse && error.status === 401 ? "No se pudo validar la contrasena o el segundo factor." : errorMessage(error));
			this.form.controls.password.reset(); this.form.controls.verification.reset();
		} finally { this.busy.set(false); this.dialog.disableClose = !!this.codes().length; }
	}
	private showRecovery(result: MfaRecovery) { this.setup.set(null); this.qr.set(""); this.form.reset(); this.codes.set(result.recoveryCodes); this.saved.set(false); }
	async finish() { this.codes.set([]); this.mode.set(null); this.saved.set(false); this.dialog.disableClose = false; await this.load(); }
	download() {
		const url = URL.createObjectURL(new Blob([this.codes().join("\n")], { type: "text/plain" }));
		const link = document.createElement("a"); link.href = url; link.download = "bankflow-recovery-codes.txt"; link.click(); URL.revokeObjectURL(url);
	}
	ngOnDestroy() { this.setup.set(null); this.qr.set(""); this.codes.set([]); this.form.reset(); }
}