import { Component, inject, signal } from "@angular/core";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from "@angular/material/dialog";
import { MatButtonModule } from "@angular/material/button";
import { MatFormFieldModule } from "@angular/material/form-field";
import { MatInputModule } from "@angular/material/input";
import { MatIconModule } from "@angular/material/icon";
import { MatSelectModule } from "@angular/material/select";
import { Banking, Account, Client } from "./banking";
import { errorMessage } from "./session";

export interface OperationData { action: "create" | "deposit" | "withdraw" | "assign"; account?: Account; clients: Client[]; }
@Component({
	selector: "app-operation-dialog",
	imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatIconModule, MatSelectModule],
	template: `
		<h2 mat-dialog-title>{{ title }}</h2>
		<form [formGroup]="form" (ngSubmit)="submit()">
			<mat-dialog-content>
				@if (data.action === 'create' || data.action === 'assign') {
					<mat-form-field appearance="outline"><mat-label>Cliente propietario</mat-label><mat-select formControlName="ownerUserId">@for (client of data.clients; track client.id) {<mat-option [value]="client.id">{{ client.email }}</mat-option>}</mat-select><mat-error>Selecciona un cliente.</mat-error></mat-form-field>
					@if (!data.clients.length) {<p class="muted">No hay clientes registrados.</p>}
				}
				@if (data.action === 'create') {
					<mat-form-field appearance="outline"><mat-label>Titular</mat-label><input matInput formControlName="customerName" maxlength="100" autocomplete="off"><mat-error>Entre 2 y 100 caracteres.</mat-error></mat-form-field>
					<mat-form-field appearance="outline"><mat-label>Numero de cuenta</mat-label><input matInput formControlName="accountNumber" maxlength="20" autocomplete="off"><mat-error>Entre 4 y 20 caracteres alfanumericos o guiones.</mat-error></mat-form-field>
				} @else if (data.action === 'assign') {
					<p class="dialog-account">{{ data.account?.customerName }}<br><span class="muted">{{ data.account?.accountNumber }}</span></p>
				} @else {
					<p class="dialog-account">{{ data.account?.customerName }}<br><span class="muted">{{ data.account?.accountNumber }}</span></p>
					<mat-form-field appearance="outline"><mat-label>Importe (EUR)</mat-label><input matInput formControlName="amount" type="number" min="0.01" step="0.01" max="1000000"><mat-error>Entre 0,01 y 1.000.000, con hasta 2 decimales.</mat-error></mat-form-field>
					@if (data.action === 'withdraw') {<p class="withdraw-note">El retiro descontara el importe del saldo disponible.</p>}
				}
				@if (error()) {<p role="alert" class="form-error">{{ error() }}</p>}
			</mat-dialog-content>
			<mat-dialog-actions align="end"><button mat-button type="button" (click)="close()" [disabled]="busy()">Cancelar</button><button mat-flat-button type="submit" [disabled]="busy()"><mat-icon>{{ data.action === 'create' ? 'add' : 'check' }}</mat-icon>{{ busy() ? 'Procesando...' : 'Confirmar' }}</button></mat-dialog-actions>
		</form>
	`
})
export class OperationDialog {
	readonly data = inject<OperationData>(MAT_DIALOG_DATA);
	private readonly dialog = inject(MatDialogRef<OperationDialog>);
	private readonly banking = inject(Banking);
	readonly busy = signal(false);
	readonly error = signal("");
	readonly title = this.data.action === "create" ? "Nueva cuenta" : this.data.action === "assign" ? "Asignar propietario" : this.data.action === "deposit" ? "Depositar fondos" : "Confirmar retiro";
	readonly form = inject(FormBuilder).nonNullable.group({
		ownerUserId: ["", ["create", "assign"].includes(this.data.action) ? [Validators.required] : []],
		customerName: ["", this.data.action === "create" ? [Validators.required, Validators.minLength(2), Validators.maxLength(100)] : []],
		accountNumber: ["", this.data.action === "create" ? [Validators.required, Validators.pattern(/^[a-zA-Z0-9-]{4,20}$/)] : []],
		amount: [0, ["deposit", "withdraw"].includes(this.data.action) ? [Validators.required, Validators.min(0.01), Validators.max(1000000), Validators.pattern(/^\d+(\.\d{1,2})?$/)] : []]
	});
	close() { if (!this.busy()) this.dialog.close(); }
	async submit() {
		if (this.busy()) return;
		if (this.form.invalid) { this.form.markAllAsTouched(); return; }
		this.busy.set(true);
		this.dialog.disableClose = true;
		this.error.set("");
		try {
			const value = this.form.getRawValue();
			const account = this.data.action === "create"
				? await this.banking.create(value.customerName.trim(), value.accountNumber.trim(), value.ownerUserId)
				: this.data.action === "assign" ? await this.banking.assignOwner(this.data.account!.id, value.ownerUserId)
				: await this.banking.operation(this.data.account!.id, this.data.action, value.amount);
			this.dialog.close(account);
		} catch (error) { this.error.set(errorMessage(error)); }
		finally { this.busy.set(false); this.dialog.disableClose = false; }
	}
}