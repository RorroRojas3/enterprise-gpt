import { Component, EventEmitter, OnInit, Output } from '@angular/core';

@Component({
  selector: 'app-navbar',
  imports: [],
  templateUrl: './navbar.component.html',
  styleUrl: './navbar.component.scss',
})
export class NavbarComponent implements OnInit {
  @Output() toggleMobileSidebar = new EventEmitter<void>();

  currentTheme = 'light';

  ngOnInit(): void {
    this.currentTheme = localStorage.getItem('theme') || 'light';
    this.applyTheme(this.currentTheme);
  }

  toggleTheme(): void {
    this.currentTheme = this.currentTheme === 'light' ? 'dark' : 'light';
    this.applyTheme(this.currentTheme);
    localStorage.setItem('theme', this.currentTheme);
  }

  onToggleMobileSidebar(): void {
    this.toggleMobileSidebar.emit();
  }

  private applyTheme(theme: string): void {
    document.documentElement.setAttribute('data-theme', theme);
  }
}
