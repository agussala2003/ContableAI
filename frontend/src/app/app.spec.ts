import { TestBed } from '@angular/core/testing';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  // Nota: se removió el test scaffolding "should render title" de Angular, que exigía
  // <h1>Hello, frontend</h1> — un texto que la app real (shell con router-outlet) nunca
  // renderiza. Era un falso rojo permanente sin valor de negocio.
});
