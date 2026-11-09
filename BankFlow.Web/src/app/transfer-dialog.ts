import { Component, inject, signal } from "@angular/core";
import { CurrencyPipe, DatePipe } from "@angular/common";
import { HttpErrorResponse } from "@angular/common/http";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from "@angular/material/dialog";
import { MatFormFieldModule } from "@angular/material/form-field";
import { MatInputModule } from "@angular/material/input";
import { MatButtonModule } from "@angular/material/button";
import { MatIconModule } from "@angular/material/icon";
import { Account, Banking, TransferDraft, TransferReceipt } from "./banking";
import { errorMessage } from "./session";

interface TransferData { account: Account; userId: string; pending: TransferDraft | null; }

@Component({
	selector: "app-transfer-dialog",
	imports: [CurrencyPipe, DatePipe, ReactiveFormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule],
	template: `
		<h2 mat-dialog-title>{{ receipt() ? 'Transferencia completada' : review() ? 'Confirmar transferencia' : 'Nueva transferencia' }}</h2>
		@if (receipt(); as result) {
			<mat-dialog-content>
				<div class="receipt-status"><mat-icon>check_circle</mat-icon><strong>{{ result.amount | currency:'EUR' }}</strong></div>
				<dl class="transfer-summary"><dt>Destino</dt><dd>{{ result.destinationAccountNumber }}</dd><dt>Concepto</dt><dd>{{ result.description || 'Transferencia interna' }}</dd><dt>Fecha</dt><dd>{{ result.createdAt | date:'dd/MM/yyyy HH:mm' }}</dd><dt>Referencia</dt><dd class="receipt-reference">{{ result.id }}</dd></dl>
			</mat-dialog-content>
			<mat-dialog-actions align="end"><button mat-stroked-button (click)="download()"><mat-icon>download</mat-icon>Comprobante</button><button mat-flat-button (click)="close()">Cerrar</button></mat-dialog-actions>
		} @else {
			<form [formGroup]="form" (ngSubmit)="review() ? send() : prepare()">
				<mat-dialog-content>
					<p class="dialog-account">Origen: {{ data.account.accountNumber }}<br><span class="muted">{{ data.account.customerName }}</span></p>
					@if (!review()) {
						<mat-form-field appearance="outline"><mat-label>Cuenta destino</mat-label><input matInput formControlName="destination" maxlength="20" autocomplete="off"><mat-error>Entre 4 y 20 caracteres alfanumericos o guiones.</mat-error></mat-form-field>
						<mat-form-field appearance="outline"><mat-label>Importe (EUR)</mat-label><input matInput formControlName="amount" type="number" min="0.01" max="1000000" step="0.01"><mat-error>Importe positivo con hasta 2 decimales.</mat-error></mat-form-field>
						<mat-form-field appearance="outline"><mat-label>Concepto</mat-label><input matInput formControlName="description" maxlength="140" autocomplete="off"></mat-form-field>
					} @else {
						<dl class="transfer-summary"><dt>Destino</dt><dd>{{ form.controls.destination.value }}</dd><dt>Importe</dt><dd>{{ form.controls.amount.value | currency:'EUR' }}</dd><dt>Concepto</dt><dd>{{ form.controls.description.value || 'Transferencia interna' }}</dd></dl>
					}
					@if (error()) {<p role="alert" class="form-error">{{ error() }}</p>}
				</mat-dialog-content>
				<mat-dialog-actions align="end"><button mat-button type="button" [disabled]="busy()" (click)="close()">Cerrar</button>@if (review() && !pending()) {<button mat-button type="button" (click)="review.set(false)">Editar</button>}<button mat-flat-button type="submit" [disabled]="busy()"><mat-icon>send</mat-icon>{{ busy() ? 'Procesando...' : pending() ? 'Reintentar transferencia' : review() ? 'Confirmar transferencia' : 'Revisar' }}</button></mat-dialog-actions>
			</form>
		}
	`
})
export class TransferDialog {
	readonly data = inject<TransferData>(MAT_DIALOG_DATA);
	private readonly dialog = inject(MatDialogRef<TransferDialog>);
	private readonly banking = inject(Banking);
	readonly pending = signal(this.data.pending);
	readonly review = signal(!!this.data.pending);
	readonly receipt = signal<TransferReceipt | null>(null);
	readonly busy = signal(false);
	readonly error = signal("");
	readonly form = inject(FormBuilder).nonNullable.group({
		destination: [this.data.pending?.destinationAccountNumber ?? "", [Validators.required, Validators.pattern(/^[a-zA-Z0-9-]{4,20}$/)]],
		amount: [this.data.pending?.amount ?? 0, [Validators.required, Validators.min(0.01), Validators.max(1000000), Validators.pattern(/^\d+(\.\d{1,2})?$/)]],
		description: [this.data.pending?.description ?? "", Validators.maxLength(140)]
	});
	prepare() {
		if (this.form.invalid) { this.form.markAllAsTouched(); return; }
		if (this.form.controls.destination.value.trim().toLowerCase() === this.data.account.accountNumber.toLowerCase()) {
			this.error.set("Selecciona una cuenta destino distinta."); return;
		}
		this.error.set("");
		this.review.set(true);
	}
	async send() {
		if (this.busy()) return;
		const previouslyPending = !!this.pending();
		this.busy.set(true);
		this.dialog.disableClose = true;
		this.error.set("");
		let submitted = false;
		try {
			const value = this.form.getRawValue();
			const draft = this.pending() ?? { key: crypto.randomUUID(), sourceAccountId: this.data.account.id,
				destinationAccountNumber: value.destination.trim(), amount: value.amount, description: value.description.trim() };
			this.banking.savePending(this.data.userId, draft);
			this.pending.set(draft);
			submitted = true;
			const receipt = await this.banking.transfer(draft);
			this.receipt.set(receipt);
			try { this.banking.clearPending(this.data.userId); } catch { }
			this.pending.set(null);
		} catch (error) {
			const code = error instanceof HttpErrorResponse ? error.error?.code : undefined;
			if (!previouslyPending && error instanceof HttpErrorResponse && [400, 401, 403, 404, 409].includes(error.status) && code !== "idempotency_conflict") {
				try { this.banking.clearPending(this.data.userId); } catch { }
				this.pending.set(null);
				this.review.set(false);
			}
			const messages: Record<string, string> = {
				insufficient_funds: "El saldo disponible es insuficiente.", balance_changed: "El saldo cambio durante la operacion. Revisa y vuelve a intentarlo.",
				idempotency_conflict: "La referencia corresponde a otra peticion. No se ha ejecutado una nueva transferencia.", destination_limit: "La cuenta destino no admite este importe."
			};
			this.error.set(code && messages[code] ? messages[code] : this.pending() && submitted
				? "No se pudo confirmar el resultado. Reintenta esta misma transferencia."
				: !submitted ? "No se pudo preparar la transferencia de forma segura." : errorMessage(error));
		} finally { this.busy.set(false); this.dialog.disableClose = !!this.receipt(); }
	}
	close() { if (!this.busy()) this.dialog.close(this.receipt()); }
	download() {
		const receipt = this.receipt();
		if (!receipt) return;
		const url = URL.createObjectURL(new Blob([JSON.stringify(receipt, null, 2)], { type: "application/json" }));
		const link = document.createElement("a");
		link.href = url;
		link.download = `bankflow-${receipt.id}.json`;
		link.click();
		URL.revokeObjectURL(url);
	}
}