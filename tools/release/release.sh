#!/usr/bin/env bash
# Releases of AD Manager (docs/dev/releasing.md). Needs git, sed, awk and python3; run it from any directory.
#
#   release.sh <version>           one-step release: the version in its four places, release notes from
#                                  docs/releases/NEXT.md, the checks and one commit "Release <version>".
#                                  Pushing that commit starts .github/workflows/release.yml.
#   release.sh prepare <version>   two-step release, step 1: the commit "Prepare <version>" (push it, wait for CI)
#   release.sh publish             step 2: the notes with the marker of the Prepare commit, "Release notes <version>"
#   release.sh check               versions, release notes and documentation checks (also the CI job "checks")
#   release.sh status              version, last tag, state of the draft, what the next push would release
#
# Options:
#   --dry-run                print what would be committed and the result of the checks; change nothing
#   --watchdog-note <text>   line of the Debian changelog entry of admng-watchdog (repeat it for more lines);
#                            default: "No change in how the service works and no database migration." and
#                            "Rebuilt for AD Manager <version>."
#   --commit <rev>           publish only: release this commit instead of the Prepare commit (e.g. a later fix)
#
# Lines for the end of the commit message (Co-Authored-By and the like) are taken from the environment variable
# ADMNG_COMMIT_TRAILERS (one per line); without it the commit has none.
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(git -C "$script_dir" rev-parse --show-toplevel)
cd "$root"

props=Directory.Build.props
changelog=packaging/admng-watchdog/debian/changelog
draft=docs/releases/NEXT.md
version_files=("$props" README.md README.pl.md "$changelog")
version_pattern='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'

dry_run=false
watchdog_notes=()
commit_rev=""
touched=()      # tracked files this run edits
created=()      # files this run creates
committed=false

die() { echo "release.sh: $*" >&2; exit 1; }
usage() { sed -n '2,/^set -euo/{/^set -euo/d;s/^# \{0,1\}//;p;}' "${BASH_SOURCE[0]}"; }

# Leaves the tree as it was unless the commit was made (dry run, failed check, any error).
restore() {
    local status=$?
    trap - EXIT
    if ! $committed && [ $(( ${#touched[@]} + ${#created[@]} )) -gt 0 ]; then
        git reset -q -- ${touched[@]+"${touched[@]}"} ${created[@]+"${created[@]}"} 2>/dev/null || true
        [ "${#touched[@]}" -eq 0 ] || git checkout -q -- "${touched[@]}"
        [ "${#created[@]}" -eq 0 ] || rm -f -- "${created[@]}"
        [ "$status" -eq 0 ] || echo "release.sh: nothing was committed; the working tree is as it was." >&2
    fi
    exit "$status"
}
trap restore EXIT

current_version() { sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$props"; }

# Fails unless <new> is major.minor.patch and greater than <current>.
validate_version() {
    local new=$1 current=$2 n1 n2 n3
    [[ "$new" =~ $version_pattern ]] \
        || die "'$new' is not a version <major>.<minor>.<patch> (digits only: no 'v', no suffix, no leading zeros)"
    n1=${BASH_REMATCH[1]} n2=${BASH_REMATCH[2]} n3=${BASH_REMATCH[3]}
    [[ "$current" =~ $version_pattern ]] || die "VersionPrefix '$current' in $props is not <major>.<minor>.<patch>"
    if (( n1 < BASH_REMATCH[1] || (n1 == BASH_REMATCH[1] && (n2 < BASH_REMATCH[2] \
        || (n2 == BASH_REMATCH[2] && n3 <= BASH_REMATCH[3]))) )); then
        die "version $new is not greater than the current version $current"
    fi
}

require_clean_tree() {
    local dirty
    dirty=$(git status --porcelain)
    [ -z "$dirty" ] || die "the working tree is not clean (commit or remove these first):
$dirty"
}

# Newest commit in the history of HEAD whose subject is exactly one of the arguments.
commit_with_subject() {
    git log --format='%H %s' | awk -v first="$1" -v second="${2:-}" '
        { hash = $1; sub(/^[^ ]+ /, "") }
        found == "" && ($0 == first || (second != "" && $0 == second)) { found = hash }
        END { print found }'
}

# sed on a file, keeping its mode: edit <file> <sed arguments...>
edit() {
    local file=$1
    shift
    sed "$@" "$file" > "$file.release-tmp"
    cat "$file.release-tmp" > "$file"
    rm -f "$file.release-tmp"
}

# replace_once <file> <what> <basic regex> <replacement>: the place must be there exactly once.
replace_once() {
    local file=$1 what=$2 pattern=$3 replacement=$4 count
    count=$(grep -c -- "$pattern" "$file" || true)
    [ "$count" = 1 ] || die "$file: expected the $what exactly once, found it $count time(s)"
    edit "$file" "s|$pattern|$replacement|"
}

watchdog_changelog_lines() {
    if [ "${#watchdog_notes[@]}" -eq 0 ]; then
        printf '  * No change in how the service works and no database migration.\n'
        printf '  * Rebuilt for AD Manager %s.\n' "$1"
        return
    fi
    printf '%s\n' "${watchdog_notes[@]}" | python3 -c '
import sys, textwrap
for line in sys.stdin:
    line = " ".join(line.split()).lstrip("*- ")
    if line:
        print(textwrap.fill(line, width=78, initial_indent="  * ", subsequent_indent="    ",
                            break_long_words=False, break_on_hyphens=False))'
}

# The four places of the manual "Prepare" commit: VersionPrefix, both README badges and preview sentences, changelog.
bump_version() {
    local old=$1 new=$2 old_re maintainer date lines
    old_re=${old//./\\.}
    touched+=("${version_files[@]}")

    replace_once "$props" "VersionPrefix $old" "<VersionPrefix>$old_re</VersionPrefix>" "<VersionPrefix>$new</VersionPrefix>"
    replace_once README.md "version badge" "badge/version-$old_re-" "badge/version-$new-"
    replace_once README.pl.md "version badge" "badge/wersja-$old_re-" "badge/wersja-$new-"
    if [[ "$new" == 0.* ]]; then
        replace_once README.md "sentence '($old is a preview release)'" \
            "($old_re is a preview release)" "($new is a preview release)"
        replace_once README.pl.md "sentence '($old jest wersją zapoznawczą)'" \
            "($old_re jest wersją zapoznawczą)" "($new jest wersją zapoznawczą)"
    elif grep -q "is a preview release)" README.md || grep -q "jest wersją zapoznawczą)" README.pl.md; then
        die "$new is a stable release: remove the preview-release sentence from README.md and README.pl.md by hand first (commit it; push it together with the release commit)"
    fi

    [ "$(awk 'NR == 1' "$changelog")" = "admng-watchdog (@VERSION@) unstable; urgency=medium" ] \
        || die "$changelog: the first line is not the entry 'admng-watchdog (@VERSION@) unstable; urgency=medium'"
    [ "$(grep -c '^admng-watchdog (@VERSION@)' "$changelog")" = 1 ] || die "$changelog: more than one '@VERSION@' entry"
    maintainer=$(awk '/^ -- / && !done { sub(/^ -- /, ""); sub(/>  .*$/, ">"); print; done = 1 }' "$changelog")
    [ -n "$maintainer" ] || die "$changelog: no ' -- maintainer <address>  date' line to copy the maintainer from"
    date=$(python3 -c '
import datetime, email.utils, os
epoch = os.environ.get("SOURCE_DATE_EPOCH")
utc = datetime.timezone.utc
now = datetime.datetime.fromtimestamp(int(epoch), utc) if epoch else datetime.datetime.now(utc)
print(email.utils.format_datetime(now))')
    lines=$(watchdog_changelog_lines "$new")
    [ -n "$lines" ] || die "--watchdog-note is empty"
    {
        printf 'admng-watchdog (@VERSION@) unstable; urgency=medium\n\n%s\n\n -- %s  %s\n\n' "$lines" "$maintainer" "$date"
        sed "1s/(@VERSION@)/($old-1)/" "$changelog"
    } > "$changelog.release-tmp"
    cat "$changelog.release-tmp" > "$changelog"
    rm -f "$changelog.release-tmp"
}

# write_notes <version> <previous> [marker]: docs/releases/NEXT.md becomes v<version>.md, NEXT.md starts again.
write_notes() {
    local version=$1 previous=$2 marker=${3:-} previous_commit arguments
    arguments=(render --version "$version" --previous "$previous")
    [ -z "$marker" ] || arguments+=(--marker "$marker")
    # "bez zmian" next to the OpenWEC packages only when their recipe is the same as at the previous release.
    previous_commit=$(commit_with_subject "Prepare $previous" "Release $previous")
    if [ -z "$previous_commit" ] \
        || ! git diff --quiet "$previous_commit" HEAD -- packaging/admng-openwec tools/packaging/build-openwec-deb.sh; then
        arguments+=(--openwec-changed)
    fi
    created+=("docs/releases/v$version.md")
    python3 tools/release/notes.py "${arguments[@]}"
    touched+=("$draft")
    python3 tools/release/notes.py reset
}

run_checks() {
    local failed=0
    python3 tools/docs/check_docs.py || failed=1
    python3 tools/release/check_release.py || failed=1
    return "$failed"
}

# finish <subject> <body>: stages what this run changed, runs the checks and commits (or shows it, with --dry-run).
finish() {
    local subject=$1 body=$2 message
    git add -- ${touched[@]+"${touched[@]}"} ${created[@]+"${created[@]}"}
    if $dry_run; then
        git --no-pager diff --cached --stat
        git --no-pager diff --cached
    fi
    run_checks || die "the checks failed"
    if $dry_run; then
        echo "Dry run: the checks pass; nothing was committed (commit message: \"$subject\")."
        return
    fi
    message=$(printf '%s\n\n%s\n' "$subject" "$body")
    if [ -n "${ADMNG_COMMIT_TRAILERS:-}" ]; then
        message=$(printf '%s\n\n%s\n' "$message" "$ADMNG_COMMIT_TRAILERS")
    fi
    git commit -q -m "$message"
    committed=true
    echo "Committed $(git rev-parse --short HEAD) \"$subject\"."
}

release_branches() {
    awk '/^ *branches: *\[/ && !done { gsub(/^[^[]*\[|\].*$|"/, ""); gsub(/, */, " "); print; done = 1 }' \
        .github/workflows/release.yml
}

warn_unless_release_branch() {
    local branch
    branch=$(git symbolic-ref -q --short HEAD || true)
    case " $(release_branches) " in
        *" $branch "*) ;;
        *) echo "Note: '${branch:-detached HEAD}' is not a release branch of release.yml ($(release_branches)): a push from here releases nothing." ;;
    esac
}

refuse_existing_release() {
    local version=$1
    [ ! -e "docs/releases/v$version.md" ] || die "docs/releases/v$version.md already exists"
    ! git rev-parse -q --verify "refs/tags/v$version" >/dev/null || die "the tag v$version already exists"
}

do_release() {
    local version=$1 old
    old=$(current_version)
    validate_version "$version" "$old"
    require_clean_tree
    refuse_existing_release "$version"
    write_notes "$version" "$old"
    bump_version "$old" "$version"
    finish "Release $version" "Version $version in $props, the README badges and the preview-release sentence in both
languages, the Debian changelog of admng-watchdog ($old-1 made concrete, new entry), the release notes
docs/releases/v$version.md from the draft and a fresh $draft. Pushing this commit starts the release workflow."
    if ! $dry_run; then
        echo "Next: git push   (the Release workflow builds, tests and packages this commit, then creates the tag v$version"
        echo "      and the GitHub release)."
        warn_unless_release_branch
    fi
}

do_prepare() {
    local version=$1 old
    old=$(current_version)
    validate_version "$version" "$old"
    require_clean_tree
    refuse_existing_release "$version"
    bump_version "$old" "$version"
    finish "Prepare $version" "Version $version in $props, the README badges and the preview-release sentence in both
languages, the Debian changelog of admng-watchdog ($old-1 made concrete, new entry). The release notes stay in
$draft until 'tools/release/release.sh publish'."
    if ! $dry_run; then
        echo "Next: git push, wait for a green CI run, then: tools/release/release.sh publish && git push"
    fi
}

do_publish() {
    local version previous prepare target later
    version=$(current_version)
    [[ "$version" =~ $version_pattern ]] || die "VersionPrefix '$version' in $props is not <major>.<minor>.<patch>"
    [ ! -e "docs/releases/v$version.md" ] \
        || die "$version already has its notes docs/releases/v$version.md: nothing to publish (first: release.sh prepare <version>)"
    require_clean_tree
    refuse_existing_release "$version"
    prepare=$(commit_with_subject "Prepare $version")
    [ -n "$prepare" ] || die "no commit 'Prepare $version' in the history of HEAD (run: release.sh prepare <version>)"
    target=$prepare
    if [ -n "$commit_rev" ]; then
        target=$(git rev-parse -q --verify "$commit_rev^{commit}") || die "--commit '$commit_rev' is not a commit"
        git merge-base --is-ancestor "$prepare" "$target" && git merge-base --is-ancestor "$target" HEAD \
            || die "--commit $commit_rev is not between the commit 'Prepare $version' and HEAD"
    fi
    [ "$(git show "$target:$props" | sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p')" = "$version" ] \
        || die "VersionPrefix at $target is not $version"
    previous=$(awk '/^admng-watchdog \(/ { n++; if (n == 2) { sub(/^admng-watchdog \(/, ""); sub(/-[0-9]+\).*$/, ""); print } }' "$changelog")
    [[ "$previous" =~ $version_pattern ]] || die "$changelog: the second entry is not a released version"
    later=$(git rev-list --count "$target..HEAD")
    if [ "$later" -gt 0 ]; then
        echo "Note: $later commit(s) after $(git rev-parse --short "$target") will not be in the release" \
            "(to release a later commit: release.sh publish --commit <rev>)."
    fi
    write_notes "$version" "$previous" "$target"
    finish "Release notes $version" "Notes from the draft with the marker of $(git rev-parse --short "$target") ($(git log -1 --format=%s "$target")), and a
fresh $draft. Pushing this commit starts the release workflow for that commit."
    if ! $dry_run; then
        echo "Next: git push   (the Release workflow builds, tests and packages $(git rev-parse --short "$target"), then creates the tag"
        echo "      v$version on it and the GitHub release)."
        warn_unless_release_branch
    fi
}

do_status() {
    local version tag newest upstream ahead marker target notes
    version=$(current_version)
    tag=$(git describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || true)
    newest=$(find docs/releases -maxdepth 1 -name 'v*.md' | sort -V | awk 'END { sub(/^.*\//, ""); print }')
    echo "Version (VersionPrefix):      $version"
    echo "Last release tag reachable:   ${tag:-none in this clone}"
    echo "Newest release notes:         ${newest:-none}"
    if [ -n "$newest" ] && [ "$tag.md" != "$newest" ] && [ -e "docs/releases/v$version.md" ] && [ "v$version.md" = "$newest" ]; then
        echo "                              (no tag ${newest%.md} here: not released yet, or the tags are not fetched - git fetch --tags)"
    fi
    echo "Draft $draft:  $(python3 tools/release/notes.py state)"
    echo "Release branches:             $(release_branches) (now on: $(git symbolic-ref -q --short HEAD || echo 'detached HEAD'))"
    [ -z "$(git status --porcelain)" ] || echo "Working tree:                 not clean (release.sh needs a clean tree)"
    upstream=$(git rev-parse -q --verify '@{upstream}' 2>/dev/null || true)
    if [ -z "$upstream" ]; then
        echo "Next push would release:      unknown (this branch has no upstream to compare with)"
        return
    fi
    ahead=$(git rev-list --count "$upstream..HEAD")
    mapfile -t notes < <(git diff --name-only --diff-filter=AM "$upstream" HEAD -- 'docs/releases/v*.md')
    if [ "${#notes[@]}" -eq 0 ]; then
        echo "Next push would release:      nothing ($ahead commit(s) to push, no release notes among them)"
        if [ ! -e "docs/releases/v$version.md" ]; then
            if [ -n "$(commit_with_subject "Prepare $version")" ]; then
                echo "                              $version is prepared: tools/release/release.sh publish"
            fi
        else
            echo "                              $version has its notes: start the next one with tools/release/release.sh <version>"
        fi
    elif [ "${#notes[@]}" -gt 1 ]; then
        echo "Next push would release:      nothing or an error - ${#notes[@]} notes change together (${notes[*]});"
        echo "                              one release per push, unless all of them already have their tags"
    else
        tag=$(basename "${notes[0]}" .md)
        marker=$(sed -n '1s/^<!-- *release-commit: *\([0-9a-fA-F]\{7,40\}\) *-->.*$/\1/p' "${notes[0]}")
        target=$(git rev-parse -q --verify "${marker:-HEAD}^{commit}" || true)
        if [ -z "$target" ] || ! git merge-base --is-ancestor "$target" HEAD; then
            echo "Next push would release:      ERROR - the release commit '$marker' of ${notes[0]} is not in the history of HEAD"
        elif git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
            if [ "$(git rev-parse "refs/tags/$tag^{commit}")" = "$target" ]; then
                echo "Next push would release:      nothing new - $tag exists; only its release description is updated"
            else
                echo "Next push would release:      ERROR - the tag $tag exists on another commit than $(git rev-parse --short "$target")"
            fi
        else
            echo "Next push would release:      $tag from $(git rev-parse --short "$target") \"$(git log -1 --format=%s "$target")\""
        fi
    fi
}

arguments=()
while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run) dry_run=true; shift ;;
        --watchdog-note) [ $# -ge 2 ] || die "--watchdog-note needs a text"; watchdog_notes+=("$2"); shift 2 ;;
        --watchdog-note=*) watchdog_notes+=("${1#*=}"); shift ;;
        --commit) [ $# -ge 2 ] || die "--commit needs a revision"; commit_rev=$2; shift 2 ;;
        --commit=*) commit_rev=${1#*=}; shift ;;
        -h|--help) usage; exit 0 ;;
        -*) die "unknown option '$1' (see --help)" ;;
        *) arguments+=("$1"); shift ;;
    esac
done

case "${arguments[0]:-}" in
    "") usage >&2; exit 2 ;;
    check) [ "${#arguments[@]}" -eq 1 ] || die "check takes no argument"; run_checks ;;
    status) [ "${#arguments[@]}" -eq 1 ] || die "status takes no argument"; do_status ;;
    publish) [ "${#arguments[@]}" -eq 1 ] || die "publish takes no version: it releases the prepared one"; do_publish ;;
    prepare) [ "${#arguments[@]}" -eq 2 ] || die "usage: release.sh prepare <version>"; do_prepare "${arguments[1]}" ;;
    *) [ "${#arguments[@]}" -eq 1 ] || die "usage: release.sh <version> (see --help)"; do_release "${arguments[0]}" ;;
esac
