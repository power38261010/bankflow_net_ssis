import { Component, inject, signal } from "@angular/core";
import { CurrencyPipe, DatePipe } from "@angular/common";
import { Router, RouterLink } from "@angular/router";
import { MatDialog } from "@angular/material/dialog";
import { MatButtonModule } from "@angular/material/button";
import { MatIconModule } from "@angular/material/icon";
import { MatTooltipModule } from "@angular/material/tooltip";
import { MatProgressBarModule } from "@angular/material/progress-bar";
import { MatPaginatorModule, MatPaginatorIntl, PageEvent } from "@angular/material/paginator";
import { Banking, Beneficiary, ScheduledPayment } from "./banking";
import { Session, errorMessage } from "./session";
import { PaymentDialog } from "./payment-dialog";

function paginatorLabels() {
	const labels = new MatPaginatorIntl(); labels.itemsPerPageLabel = "Filas por pagina";
	labels.nextPageLabel = "Pagina siguiente"; labels.previousPageLabel = "Pagina anterior";
	labels.getRangeLabel = (page, size, length) => length ? `${Math.min(page * size + 1, length)} - ${Math.min((page + 1) * size, length)} de ${length}` : "0 de 0";
	return labels;
}

@Component({
	selector: "app-payments",
	imports: [CurrencyPipe, DatePipe, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, MatProgressBarModule, MatPaginatorModule],
	providers: [{ provide: MatPaginatorIntl, useFactory: paginatorLabels }],
	template: `
		<div class="workspace"><aside class="sidebar"><a class="brand" routerLink="/"><span class="brand-mark"><mat-icon>account_balance</mat-icon></span>BankFlow</a><div class="workspace-label">BANCA / PAGOS</div><a class="nav-item" routerLink="/"><mat-icon>space_dashboard</mat-icon>Cuentas</a><a class="nav-item active" routerLink="/payments" aria-current="page"><mat-icon>event_repeat</mat-icon>Pagos</a><div class="sidebar-bottom"><div class="operator-info"><strong>{{ session.roleLabel() }}</strong><span>{{ session.user()?.email }}</span></div></div></aside>
		<div class="workspace-main"><header class="topbar"><span class="breadcrumb">Workspace / <strong>Pagos</strong></span><div class="topbar-actions"><button mat-icon-button matTooltip="Actualizar pagos" aria-label="Actualizar pagos" [disabled]="busy()" (click)="load()"><mat-icon>refresh</mat-icon></button><button mat-icon-button matTooltip="Cerrar sesion" aria-label="Cerrar sesion" (click)="logout()"><mat-icon>logout</mat-icon></button></div></header>
		<main class="content"><div class="page-heading"><div><div class="eyebrow">BENEFICIARIOS / AGENDA</div><h1>Pagos programados</h1></div><div class="heading-actions"><button mat-stroked-button (click)="open('beneficiary')"><mat-icon>person_add</mat-icon>Nuevo beneficiario</button><button mat-flat-button (click)="open('schedule')"><mat-icon>event</mat-icon>Programar pago</button></div></div>
		@if (busy()) {<mat-progress-bar mode="indeterminate" aria-label="Cargando pagos" />}
		@if (error()) {<p role="alert" class="form-error">{{ error() }}</p><button mat-stroked-button (click)="load()">Reintentar</button>}
		<section><div class="section-toolbar"><h2>Agenda <span>{{ paymentCount() }}</span></h2><span class="muted">UTC</span></div><div class="table-scroll"><table class="payment-table"><thead><tr><th>Beneficiario</th><th>Origen</th><th>Proxima fecha (UTC)</th><th>Frecuencia</th><th class="amount-cell">Importe</th><th>Estado</th><th></th></tr></thead><tbody>@for (payment of payments(); track payment.id) {<tr><td><strong>{{ payment.beneficiaryName }}</strong><div class="muted">{{ payment.destinationAccountNumber }}</div></td><td>{{ payment.sourceAccountNumber }}</td><td>{{ payment.nextRunAt | date:'dd/MM/yyyy HH:mm':'UTC' }}</td><td>{{ frequencies[payment.frequency] }}</td><td class="amount-cell">{{ payment.amount | currency:'EUR' }}</td><td><span class="payment-status" [class.payment-failed]="payment.status === 4">{{ statuses[payment.status] }}</span>@if (payment.failureCode) {<div class="muted">{{ failure(payment.failureCode) }}</div>}</td><td>@if (payment.canCancel) {<button mat-icon-button [attr.aria-label]="'Cancelar pago a ' + payment.beneficiaryName" matTooltip="Cancelar pago" (click)="open('cancel', undefined, payment)"><mat-icon>event_busy</mat-icon></button>}</td></tr>}</tbody></table></div>@if (!busy() && !payments().length) {<p class="muted">No hay pagos programados.</p>}<mat-paginator aria-label="Paginas de pagos" [length]="paymentCount()" [pageIndex]="paymentPage()" [pageSize]="20" [disabled]="busy()" (page)="pagePayments($event)" /></section>
		<section class="clients-section"><div class="section-toolbar"><h2>Mis beneficiarios <span>{{ beneficiaryCount() }}</span></h2></div><div class="beneficiary-list">@for (beneficiary of beneficiaries(); track beneficiary.id) {<div class="beneficiary-row"><mat-icon>person_outline</mat-icon><div><strong>{{ beneficiary.name }}</strong><span class="muted">{{ beneficiary.accountNumber }}</span></div><button mat-icon-button [attr.aria-label]="'Eliminar beneficiario ' + beneficiary.name" matTooltip="Eliminar beneficiario" (click)="open('archive', beneficiary)"><mat-icon>delete_outline</mat-icon></button></div>}</div>@if (!busy() && !beneficiaries().length) {<p class="muted">No hay beneficiarios guardados.</p>}<mat-paginator aria-label="Paginas de beneficiarios" [length]="beneficiaryCount()" [pageIndex]="beneficiaryPage()" [pageSize]="20" [disabled]="busy()" (page)="pageBeneficiaries($event)" /></section>
		<footer class="workspace-footer"><span>BankFlow / Payments</span><span>EUR / UTC</span></footer></main></div></div>
	`
})
export class Payments {
	readonly session = inject(Session);
	private readonly banking = inject(Banking); private readonly dialogs = inject(MatDialog); private readonly router = inject(Router);
	readonly payments = signal<ScheduledPayment[]>([]); readonly beneficiaries = signal<Beneficiary[]>([]);
	readonly paymentPage = signal(0); readonly beneficiaryPage = signal(0); readonly paymentCount = signal(0); readonly beneficiaryCount = signal(0);
	readonly busy = signal(false); readonly error = signal("");
	readonly frequencies = ["Unico", "Semanal", "Mensual"]; readonly statuses = ["Pendiente", "En ejecucion", "Completado", "Cancelado", "Fallido"];
	constructor() { void this.load(); }
	async load() {
		this.busy.set(true); this.error.set("");
		try { const [payments, beneficiaries] = await Promise.all([this.banking.payments(this.paymentPage() + 1), this.banking.beneficiaries(this.beneficiaryPage() + 1)]);
			this.payments.set(payments.items); this.paymentCount.set(payments.totalCount); this.beneficiaries.set(beneficiaries.items); this.beneficiaryCount.set(beneficiaries.totalCount);
		} catch (error) { this.error.set(errorMessage(error)); } finally { this.busy.set(false); }
	}
	open(action: "beneficiary" | "schedule" | "archive" | "cancel", beneficiary?: Beneficiary, payment?: ScheduledPayment) {
		this.dialogs.open(PaymentDialog, { data: { action, beneficiary, payment }, width: "500px", maxWidth: "calc(100vw - 32px)" }).afterClosed().subscribe((changed: boolean) => { if (changed) void this.load(); });
	}
	pagePayments(event: PageEvent) { this.paymentPage.set(event.pageIndex); void this.load(); }
	pageBeneficiaries(event: PageEvent) { this.beneficiaryPage.set(event.pageIndex); void this.load(); }
	failure(code: string) { return ({ insufficient_funds: "Saldo insuficiente", authorization_revoked: "Permiso revocado", account_unavailable: "Cuenta no disponible", temporary_failure: "Error temporal", balance_changed: "Saldo actualizado" } as Record<string, string>)[code] ?? "Operacion rechazada"; }
	async logout() { try { await this.session.logout(); await this.router.navigateByUrl("/login"); } catch (error) { this.error.set(errorMessage(error)); } }
}