#!/bin/bash
set -euo pipefail
stage=/tmp/vectorforge-publish-18.0.1
target=/var/www/mootop/wordpress/wp-content/uploads/vectorforge
cd "$stage"
sed 's/\r$//' SHA256SUMS.txt | sha256sum -c -
sudo install -d -o www-data -g www-data -m 0755 "$target"
for name in VectorForge-v18.0.1-Windows-x64-Installer.zip VectorForge-v18.0.1-Windows-x64-Portable.zip vectorforge-v18.0.1-icon.png vectorforge-v18.0.1-ui.png vectorforge-v18.0.1-flow.png update.json; do
    if [ -e "$target/$name" ]; then cmp -s "$stage/$name" "$target/$name" || { echo "Conflicting target: $name"; exit 1; }; fi
    sudo install -o www-data -g www-data -m 0644 "$stage/$name" "$target/$name"
done
if [ -e "$target/SHA256SUMS-v18.0.1.txt" ]; then cmp -s SHA256SUMS.txt "$target/SHA256SUMS-v18.0.1.txt" || exit 1; fi
sudo install -o www-data -g www-data -m 0644 SHA256SUMS.txt "$target/SHA256SUMS-v18.0.1.txt"
cd "$target"
sed 's/\r$//' SHA256SUMS-v18.0.1.txt | sha256sum -c -
cd /var/www/mootop/wordpress
sudo touch "$stage/software-before.html"
sudo chown www-data:www-data "$stage/software-before.html"
sudo -u www-data wp eval-file "$stage/publish.php"
catalog=/var/www/mootop/wordpress/wp-content/mu-plugins/mootop-vectorforge-catalog.php
if [ -e "$catalog" ]; then cmp -s "$stage/vectorforge-catalog.php" "$catalog" || exit 1; fi
sudo install -o www-data -g www-data -m 0644 "$stage/vectorforge-catalog.php" "$catalog"
sudo -u www-data wp cache flush
