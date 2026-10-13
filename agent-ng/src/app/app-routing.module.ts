import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';

// Lazy-loaded feature views + hash routing (the app is hosted embedded in the extension panel).
const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'sign-in' },
  { path: 'sign-in', loadChildren: () => import('./views/sign-in/sign-in.module').then((m) => m.SignInModule) },
  { path: 'systems', loadChildren: () => import('./views/ecm-picker/ecm-picker.module').then((m) => m.EcmPickerModule) },
  { path: 'documents', loadChildren: () => import('./views/documents/documents.module').then((m) => m.DocumentsModule) },
  { path: '**', redirectTo: 'sign-in' },
];

@NgModule({
  imports: [RouterModule.forRoot(routes, { useHash: true })],
  exports: [RouterModule]
})
export class AppRoutingModule { }
