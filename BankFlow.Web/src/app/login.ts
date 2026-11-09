import { Component, inject, signal } from "@angular/core";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { Router } from "@angular/router";
import { MatButtonModule } from "@angular/material/button";
import { MatFormFieldModule } from "@angular/material/form-field";
import { MatInputModule } from "@angular/material/input";
import { MatIconModule } from "@angular/material/icon";
import { MatProgressBarModule } from "@angular/material/progress-bar";
import { MatTooltipModule } from "@angular/material/tooltip";
import { MatCheckboxModule } from "@angular/material/checkbox";
import { HttpErrorResponse } from "@angular/common/http";
import { Session, errorMessage } from "./session";

@Component({
	selector: "app-login",
	imports: [ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatIconModule, MatProgressBarModule, MatTooltipModule, MatCheckboxModule],
	template: `
		<div class="login-page">
			<header class="login-header"><a class="brand" href="/login"><span class="brand-mark"><mat-icon>account_balance</mat-icon></span>BankFlow<span class="brand-tag">WORKSPACE</span></a><span class="secure-label"><mat-icon>lock</mat-icon> Acceso privado</span></header>
			<main class="login-main">
				<section class="login-form-area">
					<div class="eyebrow">BANCA / OPERACIONES</div>
					<h1>{{ mfaPending() ? 'Verifica tu acceso.' : 'Tu espacio de trabajo.' }}</h1>
					<p class="muted login-intro">{{ mfaPending() ? 'Segundo factor de autenticacion' : 'Inicia sesion en BankFlow.' }}</p>
					@if (mfaPending()) {
						<form [formGroup]="mfaForm" (ngSubmit)="verify()">
							<mat-form-field appearance="outline"><mat-label>{{ recovery() ? 'Codigo de recuperacion' : 'Codigo de autenticador' }}</mat-label><input matInput formControlName="code" autocomplete="one-time-code" [attr.inputmode]="recovery() ? 'text' : 'numeric'" [maxlength]="recovery() ? 32 : 6"><mat-error>Introduce un codigo valido.</mat-error></mat-form-field>
							<mat-checkbox [checked]="recovery()" (change)="recovery.set($event.checked); mfaForm.reset()">Usar codigo de recuperacion</mat-checkbox>
							@if (error()) {<p class="form-error" role="alert">{{ error() }}</p>}
							<button mat-flat-button class="login-submit" type="submit" [disabled]="busy()">{{ busy() ? 'Verificando...' : 'Verificar codigo' }}<mat-icon iconPositionEnd>verified_user</mat-icon></button>
							<button mat-button type="button" [disabled]="busy()" (click)="cancel()">Volver al login</button>
						</form>
					} @else {
					<form [formGroup]="form" (ngSubmit)="submit()">
						<mat-form-field appearance="outline"><mat-label>Correo electronico</mat-label><input matInput formControlName="email" type="email" autocomplete="username" maxlength="254">@if (form.controls.email.invalid) {<mat-error>Introduce un correo valido.</mat-error>}</mat-form-field>
						<mat-form-field appearance="outline"><mat-label>Contrasena</mat-label><input matInput formControlName="password" [type]="hidden() ? 'password' : 'text'" autocomplete="current-password" maxlength="256"><button mat-icon-button matSuffix type="button" (click)="hidden.set(!hidden())" [attr.aria-label]="hidden() ? 'Mostrar contrasena' : 'Ocultar contrasena'" [matTooltip]="hidden() ? 'Mostrar contrasena' : 'Ocultar contrasena'"><mat-icon>{{ hidden() ? 'visibility' : 'visibility_off' }}</mat-icon></button><mat-error>Introduce tu contrasena.</mat-error></mat-form-field>
						@if (error()) {<p class="form-error" role="alert"><mat-icon>error_outline</mat-icon>{{ error() }}</p>}
						<button mat-flat-button class="login-submit" [disabled]="busy()" type="submit">{{ busy() ? 'Validando acceso...' : 'Iniciar sesion' }}<mat-icon iconPositionEnd>arrow_forward</mat-icon></button>
						@if (busy()) {<mat-progress-bar mode="indeterminate" aria-label="Validando acceso" />}
					</form>
					}
					<p class="access-note"><mat-icon>admin_panel_settings</mat-icon> Acceso privado para clientes y operadores.</p>
				</section>
				<aside class="login-photo" aria-label="Edificio de oficinas"><img src="https://images.unsplash.com/photo-1486406146926-c627a92ad1ab?auto=format&fit=crop&w=1200&q=85" alt="Fachada de vidrio de un edificio de oficinas" fetchpriority="high"><div class="photo-caption"><span class="status-dot"></span>BankFlow / Operations</div></aside>
			</main>
			<footer class="login-footer"><span>BankFlow</span><span>Entorno de operaciones bancarias</span></footer>
		</div>
	`
})
export class Login {
	private readonly session = inject(Session);
	private readonly router = inject(Router);
	readonly hidden = signal(true);
	readonly busy = signal(false);
	readonly error = signal("");
	readonly mfaPending = signal(false);
	readonly recovery = signal(false);
	readonly mfaForm = inject(FormBuilder).nonNullable.group({ code: ["", [Validators.required, Validators.maxLength(32)]] });
	constructor() { void this.resume(); }
	private async resume() {
		this.busy.set(true);
		try { this.mfaPending.set((await this.session.pendingMfa()).pending); }
		catch { this.error.set("No se pudo comprobar el estado de acceso."); }
		finally { this.busy.set(false); }
	}
	readonly form = inject(FormBuilder).nonNullable.group({
		email: ["", [Validators.required, Validators.email, Validators.maxLength(254)]],
		password: ["", [Validators.required, Validators.maxLength(256)]]
	});
	async submit() {
		if (this.busy()) return;
		if (this.form.invalid) { this.form.markAllAsTouched(); return; }
		this.busy.set(true);
		this.error.set("");
		try {
			const { email, password } = this.form.getRawValue();
			const pending = await this.session.login(email.trim(), password);
			this.form.controls.password.reset();
			this.mfaPending.set(pending);
			if (!pending) await this.router.navigateByUrl("/");
		} catch (error) {
			this.error.set(errorMessage(error));
			this.form.controls.password.reset();
		} finally { this.busy.set(false); }
	}
	async verify() {
		if (this.busy()) return;
		const code = this.mfaForm.controls.code.value.trim();
		if (this.mfaForm.invalid || !this.recovery() && !/^[0-9]{6}$/.test(code)) {
			this.mfaForm.controls.code.setErrors({ invalid: true }); this.mfaForm.markAllAsTouched(); return;
		}
		this.busy.set(true); this.error.set("");
		try { await this.session.verifyMfa(code, this.recovery()); this.mfaForm.reset(); await this.router.navigateByUrl("/"); }
		catch (error) { this.error.set(error instanceof HttpErrorResponse && error.status === 401 ? "Codigo incorrecto, usado o caducado." : errorMessage(error)); this.mfaForm.reset(); }
		finally { this.busy.set(false); }
	}
	async cancel() {
		this.busy.set(true);
		try { await this.session.cancelMfa(); this.mfaPending.set(false); this.mfaForm.reset(); this.error.set(""); }
		catch (error) { this.error.set(errorMessage(error)); }
		finally { this.busy.set(false); }
	}
}