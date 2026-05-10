#include "MainWindow.h"

#include <QApplication>
#include <QLibraryInfo>
#include <QLocale>
#include <QTranslator>

int main(int argc, char *argv[])
{
    QApplication app(argc, argv);
    app.setApplicationName(QStringLiteral("PVZOLBrowser"));
    app.setOrganizationName(QStringLiteral("PVZOL"));
    QLocale::setDefault(QLocale(QLocale::Chinese, QLocale::China));

    QTranslator translator;
    const bool loaded = translator.load(
        QLocale(QLocale::Chinese, QLocale::China),
        QStringLiteral("qtbase"),
        QStringLiteral("_"),
        QLibraryInfo::path(QLibraryInfo::TranslationsPath));
    if (loaded) {
        app.installTranslator(&translator);
    }

    MainWindow window;
    window.show();

    return app.exec();
}
