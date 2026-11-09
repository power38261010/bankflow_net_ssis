import { Component, inject, signal } from "@angular/core";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from "@angular/material/dialog";
import { MatButtonModule } from "@angular/material/button";
import { MatFormFieldModule } from "@angular/material/form-field";
import { MatInputModule } from "@angular/material/input";
import { MatSelectModule } from "@angular/material/select";
import { MatIconModule } from "@angular/material/icon";
import { MatTooltipModule } from "@angular/material/tooltip";
import { HttpErrorResponse } from "@angular/common/http";
import { Banking, Account, Beneficiary, ScheduledPayment } from "./banking";
import { Session, errorMessage } from "./session";

interface PaymentData { action: "beneficiary" | "schedule" | "archive" | "cancel"; account?: Account; beneficiary?: Beneficiary; payment?: ScheduledPayment; }

@Component({
	selector: "app-payment-dialog",
	imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatIconModule, MatTooltipModule],
	template: `
		<h2 mat-dialog-title>{{ title }}</h2>
		<form [formGroup]="form" (ngSubmit)="submit()">
			<mat-dialog-content>
				@if (data.action === 'beneficiary') {
					<mat-form-field appearance="outline"><mat-label>Nombre del beneficiario</mat-label><input matInput formControlName="name" maxlength="80"><mat-error>Introduce un nombre.</mat-error></mat-form-field>
					<mat-form-field appearance="outline"><mat-label>Cuenta destino</mat-label><input matInput formControlName="number" maxlength="20" autocomplete="off"><mat-error>Cuenta alfanumerica de 4 a 20 caracteres.</mat-error></mat-form-field>
				} @else if (data.action === 'schedule') {
					@if (pending()) {<p class="form-error">Programacion pendiente de confirmacion</p>}
					@if (data.account) {<p class="dialog-account">Origen: {{ data.account.accountNumber }}</p>} @else {
						<mat-form-field appearance="outline"><mat-label>Cuenta origen</mat-label><mat-select formControlName="source">@for (account of accounts(); track account.id) {@if (account.ownerUserId) {<mat-option [value]="account.id">{{ account.accountNumber }} / {{ account.customerName }}</mat-option>}}</mat-select><mat-error>Selecciona origen.</mat-error></mat-form-field>
						<div class="selector-pages"><button mat-icon-button type="button" aria-label="Cuentas anteriores" [disabled]="accountPage() <= 1 || busy()" (click)="loadAccounts(-1)"><mat-icon>chevron_left</mat-icon></button><span>{{ accountPage() }}</span><button mat-icon-button type="button" aria-label="Mas cuentas" [disabled]="accountPage() * 20 >= accountCount() || busy()" (click)="loadAccounts(1)"><mat-icon>chevron_right</mat-icon></button></div>
					}
					<mat-form-field appearance="outline"><mat-label>Beneficiario</mat-label><mat-select formControlName="beneficiary">@for (beneficiary of beneficiaries(); track beneficiary.id) {<mat-option [value]="beneficiary.id">{{ beneficiary.name }} / {{ beneficiary.accountNumber }}</mat-option>}</mat-select><mat-error>Selecciona beneficiario.</mat-error></mat-form-field>
					<div class="selector-pages"><button mat-icon-button type="button" aria-label="Beneficiarios anteriores" [disabled]="beneficiaryPage() <= 1 || busy()" (click)="loadBeneficiaries(-1)"><mat-icon>chevron_left</mat-icon></button><span>{{ beneficiaryPage() }}</span><button mat-icon-button type="button" aria-label="Mas beneficiarios" [disabled]="beneficiaryPage() * 20 >= beneficiaryCount() || busy()" (click)="loadBeneficiaries(1)"><mat-icon>chevron_right</mat-icon></button></div>
					<mat-form-field appearance="outline"><mat-label>Importe (EUR)</mat-label><input matInput type="number" formControlName="amount" min="0.01" max="1000000" step="0.01"><mat-error>Importe positivo, hasta dos decimales.</mat-error></mat-form-field>
					<mat-form-field appearance="outline"><mat-label>Fecha y hora local</mat-label><input matInput type="datetime-local" step="1" formControlName="due"><mat-error>Selecciona una fecha futura.</mat-error></mat-form-field>
					<mat-form-field appearance="outline"><mat-label>Frecuencia</mat-label><mat-select formControlName="frequency"><mat-option [value]="0">Unico</mat-option><mat-option [value]="1">Semanal</mat-option><mat-option [value]="2">Mensual</mat-option></mat-select></mat-form-field>
					<mat-form-field appearance="outline"><mat-label>Concepto</mat-label><input matInput formControlName="description" maxlength="140"></mat-form-field>
				} @else {
					<p>{{ data.action === 'archive' ? data.beneficiary?.name : data.payment?.beneficiaryName }}</p>
					@if (data.action === 'archive') {<p class="muted">Los pagos ya programados conservaran su destino.</p>}
				}
				@if (error()) {<p class="form-error" role="alert">{{ error() }}</p>}
			</mat-dialog-content>
			<mat-dialog-actions align="end"><button mat-button type="button" [disabled]="busy()" (click)="dialog.close()">Cerrar</button><button mat-flat-button type="submit" [disabled]="busy()"><mat-icon>{{ data.action === 'schedule' ? 'event' : data.action === 'beneficiary' ? 'person_add' : 'check' }}</mat-icon>{{ busy() ? 'Procesando...' : pending() ? 'Reintentar programacion' : 'Confirmar' }}</button></mat-dialog-actions>
		</form>
	`
})
export class PaymentDialog {
	readonly data = inject<PaymentData>(MAT_DIALOG_DATA);
	readonly dialog = inject(MatDialogRef<PaymentDialog>);
	private readonly banking = inject(Banking);
	private readonly userId = inject(Session).user()!.id;
	readonly pending = signal(this.data.action === "schedule" ? this.banking.pendingSchedule(this.userId) : null);
	readonly title = ({ beneficiary: "Nuevo beneficiario", schedule: "Programar pago", archive: "Eliminar beneficiario", cancel: "Cancelar pago" })[this.data.action];
	readonly busy = signal(false); readonly error = signal("");
	readonly accounts = signal<Account[]>([]); readonly beneficiaries = signal<Beneficiary[]>([]);
	readonly accountPage = signal(1); readonly beneficiaryPage = signal(1); readonly accountCount = signal(0); readonly beneficiaryCount = signal(0);
	readonly form = inject(FormBuilder).nonNullable.group({
		name: ["", this.data.action === "beneficiary" ? [Validators.required, Validators.maxLength(80)] : []],
		number: ["", this.data.action === "beneficiary" ? [Validators.required, Validators.pattern(/^[a-zA-Z0-9-]{4,20}$/)] : []],
		source: [this.data.account?.id ?? "", this.data.action === "schedule" ? [Validators.required] : []],
		beneficiary: ["", this.data.action === "schedule" ? [Validators.required] : []],
		amount: [0, this.data.action === "schedule" ? [Validators.min(0.01), Validators.max(1000000), Validators.pattern(/^\d+(\.\d{1,2})?$/)] : []],
		due: ["", this.data.action === "schedule" ? [Validators.required] : []],
		frequency: [0], description: ["", Validators.maxLength(140)]
	});
	constructor() {
		const pending = this.pending();
		if (pending) {
			const due = new Date(pending.dueAt);
			const local = new Date(due.getTime() - due.getTimezoneOffset() * 60000).toISOString().slice(0, 19);
			this.form.patchValue({ source: pending.sourceAccountId, beneficiary: pending.beneficiaryId, amount: pending.amount,
				description: pending.description, frequency: pending.frequency, due: local });
			this.form.disable();
		}
		if (this.data.action === "schedule") void this.initialize();
	}
	private async initialize() {
		await this.loadBeneficiaries(0);
		if (this.pending()) {
			this.busy.set(true);
			try { this.data.account = await this.banking.account(this.pending()!.sourceAccountId); }
			catch (error) { this.error.set(errorMessage(error)); } finally { this.busy.set(false); }
		} else if (!this.data.account) await this.loadAccounts(0);
	}
	async loadAccounts(delta: number) {
		this.busy.set(true);
		try { const page = await this.banking.accountPage(this.accountPage() + delta, 20, ""); this.accounts.set(page.items); this.accountPage.set(page.page); this.accountCount.set(page.totalCount); }
		catch (error) { this.error.set(errorMessage(error)); } finally { this.busy.set(false); }
	}
	async loadBeneficiaries(delta: number) {
		this.busy.set(true);
		try { const page = await this.banking.beneficiaries(this.beneficiaryPage() + delta); this.beneficiaries.set(page.items); this.beneficiaryPage.set(page.page); this.beneficiaryCount.set(page.totalCount); }
		catch (error) { this.error.set(errorMessage(error)); } finally { this.busy.set(false); }
	}
	async submit() {
		if (this.busy()) return;
		if (this.form.invalid) { this.form.markAllAsTouched(); return; }
		this.busy.set(true); this.dialog.disableClose = true; this.error.set("");
		const previouslyPending = !!this.pending();
		try {
			const value = this.form.getRawValue();
			if (this.data.action === "beneficiary") await this.banking.createBeneficiary(value.name.trim(), value.number.trim());
			else if (this.data.action === "archive") await this.banking.archiveBeneficiary(this.data.beneficiary!.id);
			else if (this.data.action === "cancel") await this.banking.cancelPayment(this.data.payment!.id);
			else {
				const due = new Date(value.due);
				if (!this.pending() && (!Number.isFinite(due.getTime()) || due.getTime() <= Date.now())) throw new Error("Selecciona una fecha futura.");
				const draft = this.pending() ?? { key: crypto.randomUUID(), sourceAccountId: value.source, beneficiaryId: value.beneficiary,
					amount: value.amount, description: value.description.trim(), frequency: value.frequency, dueAt: due.toISOString() };
				this.banking.saveSchedule(this.userId, draft); this.pending.set(draft); this.form.disable();
				await this.banking.schedule(draft);
				try { this.banking.clearSchedule(this.userId); } catch { }
				this.pending.set(null);
			}
			this.dialog.close(true);
		} catch (error) {
			if (!previouslyPending && error instanceof HttpErrorResponse && [400, 401, 403, 404].includes(error.status)) {
				try { this.banking.clearSchedule(this.userId); } catch { } this.pending.set(null); this.form.enable();
			}
			this.error.set(this.pending() ? "No se pudo confirmar la programacion. Reintenta la misma peticion." :
				error instanceof Error && !("status" in error) ? error.message : errorMessage(error));
		}
		finally { this.busy.set(false); this.dialog.disableClose = false; }
	}
}