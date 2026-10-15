import { NgModule } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { BrowserModule } from '@angular/platform-browser';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { TranslateLoader, TranslateModule } from '@ngx-translate/core';
import { TRANSLATE_HTTP_LOADER_CONFIG, TranslateHttpLoader } from '@ngx-translate/http-loader';
import { provideSatori } from '@hylandsoftware/satori-ui/providers';

import { AppRoutingModule } from './app-routing.module';
import { AppComponent } from './app.component';

@NgModule({
  declarations: [
    AppComponent
  ],
  imports: [
    BrowserModule,
    BrowserAnimationsModule,
    AppRoutingModule,
    TranslateModule.forRoot({
      fallbackLang: 'en',
      lang: 'en',
      loader: { provide: TranslateLoader, useClass: TranslateHttpLoader }
    })
  ],
  providers: [
    provideSatori(),
    provideHttpClient(),
    { provide: TRANSLATE_HTTP_LOADER_CONFIG, useValue: { prefix: 'i18n/', suffix: '.json' } }
  ],
  bootstrap: [AppComponent]
})
export class AppModule { }
