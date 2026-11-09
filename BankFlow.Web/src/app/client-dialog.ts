import { Component, inject, signal } from "@angular/core";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { MatDialogModule, MatDialogRef } from "@angular/material/dialog";
import { MatButtonModule } from "@angular/material/button";
import { MatFormFieldModule } from "@angular/material/form-field";
import { MatInputModule } from "@angular/material/input";
import { MatIconModule } from "@angular/material/icon";
import { Banking } from "./banking";
import { errorMessage } from "./session";

@Component({
	selector: "app-client-dialog",
	imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatIconModule],
	template: `
		<h2 mat-dialog-title>Nuevo cliente</h2>
		<form [formGroup]="form" (ngSubmit)="submit()">
			<mat-dialog-content>
				<mat-form-field appearance="outline"><mat-label>Correo del cliente</mat-label><input matInput type="email" formControlName="email" maxlength="254" autocomplete="off"><mat-error>Introduce un correo valido.</mat-error></mat-form-field>
				<mat-form-field appearance="outline"><mat-label>Contrasena inicial</mat-label><input matInput type="password" formControlName="password" maxlength="256" autocomplete="new-password"><mat-error>Minimo 12 caracteres, mayuscula, minuscula, numero y simbolo.</mat-error></mat-form-field>
				<mat-form-field appearance="outline"><mat-label>Confirmar contrasena</mat-label><input matInput type="password" formControlName="confirmation" maxlength="256" autocomplete="new-password"><mat-error>Confirma la contrasena.</mat-error></mat-form-field>
				@if (error()) {<p role="alert" class="form-error">{{ error() }}</p>}
			</mat-dialog-content>
			<mat-dialog-actions align="end"><button mat-button type="button" [disabled]="busy()" (click)="dialog.close()">Cancelar</button><button mat-flat-button type="submit" [disabled]="busy()"><mat-icon>person_add</mat-icon>{{ busy() ? 'Creando...' : 'Crear cliente' }}</button></mat-dialog-actions>
		</form>
	`
})
export class ClientDialog {
	readonly dialog = inject(MatDialogRef<ClientDialog>);
	private readonly banking = inject(Banking);
	readonly busy = signal(false);
	readonly error = signal("");
	readonly form = inject(FormBuilder).nonNullable.group({
		email: ["", [Validators.required, Validators.email, Validators.maxLength(254)]],
		password: ["", [Validators.required, Validators.maxLength(256), Validators.pattern(/^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z0-9]).{12,256}$/)]],
		confirmation: ["", Validators.required]
	});
	async submit() {
		if (this.busy()) return;
		if (this.form.invalid) { this.form.markAllAsTouched(); return; }
		const { email, password, confirmation } = this.form.getRawValue();
		if (password !== confirmation) { this.error.set("Las contrasenas no coinciden."); return; }
		this.busy.set(true);
		this.dialog.disableClose = true;
		this.error.set("");
		try {
			const client = await this.banking.createClient(email.trim(), password);
			this.form.reset();
			this.dialog.close(client);
		} catch (error) { this.error.set(errorMessage(error)); }
		finally { this.busy.set(false); this.dialog.disableClose = false; }
	}
}