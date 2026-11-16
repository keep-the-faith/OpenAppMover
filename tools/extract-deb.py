#!/usr/bin/env python3
"""Extracts Debian packages (.deb) into a folder, like "dpkg -x", but copies hard links instead of creating them
(some file systems, such as the one of a build sandbox, do not support them).
Usage: extract-deb.py <destination> <package.deb>..."""
import io
import os
import shutil
import subprocess
import sys
import tarfile

destination = sys.argv[1]
for deb in sys.argv[2:]:
    data = subprocess.run(["dpkg-deb", "--fsys-tarfile", deb], check=True, capture_output=True).stdout
    with tarfile.open(fileobj=io.BytesIO(data)) as archive:
        links = []
        for member in archive:
            if member.islnk():
                links.append(member)
            else:
                archive.extract(member, destination, filter="tar")
        for member in links:
            target = os.path.join(destination, member.name)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copy2(os.path.join(destination, member.linkname), target)
