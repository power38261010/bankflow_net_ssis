import { Component, inject, signal } from "@angular/core";
import { CurrencyPipe, DatePipe } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { Router, RouterLink } from "@angular/router";
import { MatButtonModule } from "@angular/material/button";
import { MatIconModule } from "@angular/material/icon";
import { MatFormFieldModule } from "@angular/material/form-field";
import { MatInputModule } from "@angular/material/input";
import { MatDialog } from "@angular/material/dialog";
import { MatSnackBar } from "@angular/material/snack-bar";
import { MatTooltipModule } from "@angular/material/tooltip";
import { MatProgressBarModule } from "@angular/material/progress-bar";
import { Account, Banking, Client, Transaction, TransferDraft, TransferReceipt } from "./banking";
import { Session, errorMessage } from "./session";
import { OperationData, OperationDialog } from "./operation-dialog";
import { ClientDialog } from "./client-dialog";
import { TransferDialog } from "./transfer-dialog";
import { MatPaginatorModule, MatPaginatorIntl, PageEvent } from "@angular/material/paginator";
import { Subject, debounceTime, distinctUntilChanged } from "rxjs";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { MatMenuModule } from "@angular/material/menu";
import { MatBadgeModule } from "@angular/material/badge";
import { NotificationFeed } from "./notification-feed";

function spanishPaginator() {
	const labels = new MatPaginatorIntl();
	labels.itemsPerPageLabel = "Filas por pagina";
	labels.nextPageLabel = "Pagina siguiente";
	labels.previousPageLabel = "Pagina anterior";
	labels.getRangeLabel = (page, size, length) => length === 0 ? "0 de 0" : `${Math.min(page * size + 1, length)} - ${Math.min((page + 1) * size, length)} de ${length}`;
	return labels;
}

@Component({
	selector: "app-dashboard",
	imports: [CurrencyPipe, DatePipe, FormsModule, RouterLink, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule, MatTooltipModule, MatProgressBarModule, MatPaginatorModule, MatMenuModule, MatBadgeModule],
	providers: [{ provide: MatPaginatorIntl, useFactory: spanishPaginator }],
	templateUrl: "./dashboard.html"
})
export class Dashboard {
	readonly session = inject(Session);
	private readonly banking = inject(Banking);
	private readonly router = inject(Router);
	private readonly dialogs = inject(MatDialog);
	private readonly snackbar = inject(MatSnackBar);
	readonly notificationFeed = inject(NotificationFeed);
	readonly accounts = signal<Account[]>([]);
	readonly clients = signal<Client[]>([]);
	readonly search = signal("");
	readonly pageIndex = signal(0);
	readonly pageSize = signal(20);
	readonly accountCount = signal(0);
	readonly matchingCount = signal(0);
	readonly total = signal(0);
	readonly movementPageIndex = signal(0);
	readonly movementPageSize = signal(20);
	readonly movementCount = signal(0);
	private readonly searches = new Subject<string>();
	private accountRequest = 0;
	private historyRequest = 0;
	readonly loading = signal(false);
	readonly error = signal("");
	readonly selected = signal<Account | null>(null);
	readonly transactions = signal<Transaction[]>([]);
	readonly historyLoading = signal(false);
	readonly historyError = signal("");
	readonly loggingOut = signal(false);
	readonly pendingTransfer = signal<TransferDraft | null>(this.banking.pendingTransfer(this.session.user()!.id));
	readonly filtered = this.accounts;
	constructor() {
		void this.notificationFeed.connect();
		this.searches.pipe(debounceTime(250), distinctUntilChanged(), takeUntilDestroyed()).subscribe(() => {
			this.pageIndex.set(0); void this.load();
		});
		void this.load();
	}
	searchAccounts(value: string) {
		this.search.set(value); this.accountRequest++; this.searches.next(value);
	}
	changePage(event: PageEvent) {
		this.pageIndex.set(event.pageIndex); this.pageSize.set(event.pageSize); void this.load();
	}
	async load() {
		const request = ++this.accountRequest;
		this.loading.set(true);
		this.error.set("");
		try {
			const [page, summary, clients] = await Promise.all([
				this.banking.accountPage(this.pageIndex() + 1, this.pageSize(), this.search()), this.banking.summary(),
				this.session.isOperator() ? this.banking.clients() : Promise.resolve([])
			]);
			if (request !== this.accountRequest) return;
			if (!page.items.length && page.totalCount > 0 && this.pageIndex() > 0) {
				this.pageIndex.set(Math.max(0, Math.ceil(page.totalCount / this.pageSize()) - 1)); void this.load(); return;
			}
			this.accounts.set(page.items);
			this.matchingCount.set(page.totalCount);
			this.accountCount.set(summary.accountCount);
			this.total.set(summary.totalBalance);
			this.clients.set(clients);
		}
		catch (error) { if (request === this.accountRequest) this.error.set(errorMessage(error)); }
		finally { if (request === this.accountRequest) this.loading.set(false); }
	}
	async history(account: Account, resetPage = true) {
		const request = ++this.historyRequest;
		if (resetPage) this.movementPageIndex.set(0);
		this.selected.set(account);
		this.transactions.set([]);
		this.historyLoading.set(true);
		this.historyError.set("");
		try {
			const page = await this.banking.movementPage(account.id, this.movementPageIndex() + 1, this.movementPageSize());
			if (request === this.historyRequest && this.selected()?.id === account.id) {
				this.transactions.set(page.items); this.movementCount.set(page.totalCount);
			}
		} catch (error) { if (request === this.historyRequest && this.selected()?.id === account.id) this.historyError.set(errorMessage(error)); }
		finally { if (request === this.historyRequest && this.selected()?.id === account.id) this.historyLoading.set(false); }
	}
	changeMovementPage(event: PageEvent) {
		this.movementPageIndex.set(event.pageIndex); this.movementPageSize.set(event.pageSize);
		if (this.selected()) void this.history(this.selected()!, false);
	}
	operate(action: OperationData["action"], account?: Account) {
		if (!this.session.isOperator()) return;
		this.dialogs.open(OperationDialog, { data: { action, account, clients: this.clients() }, width: "460px", maxWidth: "calc(100vw - 32px)" }).afterClosed().subscribe(async (updated?: Account) => {
			if (!updated) return;
			if (action === "create") this.pageIndex.set(0);
			await this.load();
			this.snackbar.open("Operacion completada", "Cerrar", { duration: 4000 });
			if (this.selected()?.id === updated.id) void this.history(updated);
		});
	}
	ownerEmail(account: Account) {
		return this.clients().find(client => client.id === account.ownerUserId)?.email ?? (account.ownerUserId ? "Cliente asignado" : "Sin asignar");
	}
	createClient() {
		if (!this.session.isOperator()) return;
		this.dialogs.open(ClientDialog, { width: "460px", maxWidth: "calc(100vw - 32px)" }).afterClosed().subscribe((client?: Client) => {
			if (!client) return;
			this.clients.update(clients => [...clients, client]);
			this.snackbar.open("Cliente creado", "Cerrar", { duration: 4000 });
		});
	}
	async security() {
		const { MfaDialog } = await import("./mfa-dialog");
		this.dialogs.open(MfaDialog, { width: "520px", maxWidth: "calc(100vw - 32px)" });
	}
	async schedule(account: Account) {
		const { PaymentDialog } = await import("./payment-dialog");
		this.dialogs.open(PaymentDialog, { data: { action: "schedule", account }, width: "500px", maxWidth: "calc(100vw - 32px)" })
			.afterClosed().subscribe((created: boolean) => { if (created) this.snackbar.open("Pago programado", "Cerrar", { duration: 4000 }); });
	}
	async logout() {
		if (this.loggingOut()) return;
		this.loggingOut.set(true);
		try { await this.session.logout(); await this.notificationFeed.disconnect(); await this.router.navigateByUrl("/login"); }
		catch (error) { this.snackbar.open(errorMessage(error), "Cerrar", { duration: 6000 }); }
		finally { this.loggingOut.set(false); }
	}
	async transfer(account?: Account) {
		const userId = this.session.user()!.id;
		const pending = this.pendingTransfer();
		let source = account;
		try { if (pending) source = await this.banking.account(pending.sourceAccountId); }
		catch (error) { this.snackbar.open(errorMessage(error), "Cerrar", { duration: 5000 }); return; }
		if (!source?.ownerUserId) return;
		this.dialogs.open(TransferDialog, { data: { account: source, userId, pending },
			width: "480px", maxWidth: "calc(100vw - 32px)" }).afterClosed().subscribe(async (receipt?: TransferReceipt) => {
			this.pendingTransfer.set(this.banking.pendingTransfer(userId));
			if (!receipt) return;
			await this.load();
			if (this.selected()) {
				const updated = this.accounts().find(account => account.id === this.selected()?.id);
				if (updated) await this.history(updated);
			}
		});
	}
	isIncoming(transaction: Transaction) { return ["Deposit", "TransferIn"].includes(transaction.type); }
	transactionLabel(transaction: Transaction) {
		return ({ Deposit: "Deposito", Withdrawal: "Retiro", TransferIn: "Transferencia recibida", TransferOut: "Transferencia enviada" } as Record<string, string>)[transaction.type] ?? transaction.type;
	}
}